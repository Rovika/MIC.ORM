using Mic.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Mic.Linq.ExpressionTree;
using Mic.Linq.Nodes;
using Mic.Linq.Translation;
using Mic.Orm.Db.Provider;
using Mic.Orm.Db.Sql;
using Mic.Orm.Model;
using Mic.Orm.Model.Construction;

namespace Mic.Orm.Db.Model.Construction;

/// <summary>Gives every view MIC manages its defining select for one database: the SQL the module wrote for the
/// server, or the translation of the view's query.</summary>
/// <remarks>Runs after the tables have their names, since a translated select is written against them. A query's
/// select is rendered with every value as a literal; a query that takes a parameter, or that selects values the
/// entity's members do not account for, is an error on the entity.</remarks>
internal class ViewSourceBuilder {
  readonly BuilderContext _context;
  readonly DbProvider _provider;
  readonly DbModel _dbModel;

  public ViewSourceBuilder(BuilderContext context, DbProvider provider, DbModel dbModel) {
    _context = context;
    _provider = provider;
    _dbModel = dbModel;
  }

  public void Build() {
    foreach (var table in _dbModel.Tables) {
      if (table.View is not { IsManaged: true } view || table.Entity?.View is not { } definition)
        continue;
      try {
        var tables = new List<DbTableInfo>();
        if (definition.Kind == ViewDefinitionKind.Sql)
          view.Source = definition.FindSql(_provider.ServerKind)
            ?? throw new InvalidOperationException($"no SQL is written for {_provider.ServerKind}.");
        else
          (view.Source, view.ColumnNames) = TranslateQuery(definition, table, tables);
        foreach (var dependency in definition.DependsOn)
          if (!dependency.IsView && _dbModel.GetTable(dependency) is { } declared && !tables.Contains(declared))
            tables.Add(declared);
        view.ReadsTables = tables;
      } catch (Exception ex) {
        _context.AddError($"the view cannot be built for '{_dbModel.Name}': {ex.Message}", table.Entity.Name);
      }
    }
  }

  /// <summary>Translates a view's query into its select, lists the view's columns in the select's order, and adds
  /// the tables the select reads.</summary>
  private (string Sql, IReadOnlyList<string> ColumnNames) TranslateQuery(ViewInfo definition, DbTableInfo table,
      List<DbTableInfo> tables) {
    var translation = Translate(definition);
    if (translation.ResultInfo.Kind is not (QueryResultKind.Entities or QueryResultKind.Rows))
      throw new InvalidOperationException("the query returns a value, and a view is rows.");
    var sql = RenderWithLiterals(SqlBuilder.Build(translation, _dbModel));
    CollectTables(translation.SqlRoot, tables);
    foreach (var cte in translation.Ctes)
      CollectTables(cte, tables);
    return (sql, MapColumns(translation.ResultInfo.Members, table));
  }

  /// <summary>Adds the tables, not views, that the SQL tree's table nodes stand for.</summary>
  private void CollectTables(QueryNode node, List<DbTableInfo> tables) {
    if (node is TableNode { Entity: EntityTypeInfo entity } && _dbModel.GetTable(entity) is { IsView: false } read
        && !tables.Contains(read))
      tables.Add(read);
    if (node.HasChildren)
      foreach (var child in node.Children)
        CollectTables(child, tables);
  }

  private TranslatedQuery Translate(ViewInfo definition) {
    // A view's select is fixed text; the filters the extensions attach are per operation, and stay out of it.
    if (definition.TypedQuery is { } typed) {
      new SetBinder(_context).Visit(typed.Expression);
      var root = new IgnoreFiltersNode(ExpressionTreeConverter.Convert(typed.Expression), extensionType: null);
      return QueryTranslator.Translate(ExpressionTreeScanner.Scan(typed.Expression), root, _dbModel);
    }
    var buildQuery = definition.QueryBuilder ?? throw new InvalidOperationException("the view has no query.");
    var byNameRoot = new IgnoreFiltersNode(buildQuery(_dbModel), extensionType: null);
    return QueryTranslator.Translate(new QueryInfo(0, []), byNameRoot, _dbModel);
  }

  /// <summary>Renders a template with every placeholder written as a literal, since a view's select is fixed.
  /// </summary>
  private string RenderWithLiterals(SqlTemplate template) {
    var rendered = new string[template.Placeholders.Count];
    foreach (var placeholder in template.Placeholders) {
      if (placeholder.ArgumentIndex >= 0 || placeholder.ValueGetter != null)
        throw new InvalidOperationException(
          $"the query takes a parameter ('{placeholder.Name}'); a view's query is fixed, with every value written in.");
      var value = placeholder.FixedValue == null ? null : _provider.AdaptParameterValue(placeholder.FixedValue);
      rendered[placeholder.Index] = _provider.SqlDialect.GetLiteral(value)
        ?? throw new InvalidOperationException($"the value of '{placeholder.Name}' cannot be written as a literal.");
    }
    return template.Render(rendered);
  }

  /// <summary>Maps the values the select names, in its order, to the view's columns, refusing a value the entity has
  /// no member for and a member the select gives nothing.</summary>
  private static IReadOnlyList<string> MapColumns(IReadOnlyList<Mic.Linq.Model.ILinqEntityMember> selected,
      DbTableInfo table) {
    var entity = table.Entity!;
    var columns = new List<string>();
    foreach (var value in selected) {
      var member = entity.FindMember(value.Name)
        ?? throw new InvalidOperationException($"the query selects '{value.Name}', which the view has no member for.");
      var column = table.GetColumn(member)
        ?? throw new InvalidOperationException($"the query selects '{value.Name}', which is no column of the view.");
      columns.Add(column.Name);
    }
    var unselected = table.Columns.Where(c => !columns.Contains(c.Name)).Select(c => $"'{c.Member?.Name}'").ToList();
    if (unselected.Count > 0)
      throw new InvalidOperationException($"the query selects nothing for {string.Join(", ", unselected)}.");
    return columns;
  }

  /// <summary>Binds each set of a view's typed query to the model's entity of its interface: a set written in the
  /// query, and a set or a query over one held in a local the query captures.</summary>
  private sealed class SetBinder(BuilderContext context) : ExpressionVisitor {
    protected override Expression VisitConstant(ConstantExpression node) {
      if (node.Value is ViewEntitySet set)
        Bind(set);
      return base.VisitConstant(node);
    }

    protected override Expression VisitMember(MemberExpression node) {
      // A local the query captures is a field of the compiler's closure object, which is a constant.
      if (node.Expression is ConstantExpression { Value: { } closure })
        switch (GetValue(node.Member, closure)) {
          case ViewEntitySet set: Bind(set); break;
          case IQueryable query: Visit(query.Expression); break;
        }
      return base.VisitMember(node);
    }

    private void Bind(ViewEntitySet set) =>
      set.Bind(context.Model.FindEntity(set.EntityType)
        ?? throw new InvalidOperationException($"the query reads '{set.EntityType.Name}', which is not an entity of this application."));

    private static object? GetValue(MemberInfo member, object closure) =>
      member switch {
        FieldInfo field => field.GetValue(closure),
        PropertyInfo property => property.GetValue(closure),
        _ => null,
      };
  }
}
