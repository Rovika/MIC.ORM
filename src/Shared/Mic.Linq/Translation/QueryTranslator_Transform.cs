using System.Linq.Expressions;
using Mic.Common;
using Mic.Linq.Model;
using Mic.Linq.Nodes;

namespace Mic.Linq.Translation;

// ----- phase 2: the transform into the SQL tree, top-down, replacing nodes --------------------------------------------

public sealed partial class QueryTranslator {
  // What each lambda parameter stands for: a row, or for a GroupJoin's second parameter the correlated sub-select.
  readonly Dictionary<LambdaParameterNode, QueryNode> _rows = new(ReferenceEqualityComparer.Instance);
  // The built value of each slot of each bundle, and the selects a bundle was built in, which cannot become derived
  // tables.
  readonly Dictionary<BundleShape, IReadOnlyList<QueryNode>> _bundles = new(ReferenceEqualityComparer.Instance);
  readonly HashSet<SelectNode> _bundled = new(ReferenceEqualityComparer.Instance);
  // The filter an extension attaches to a table, instantiated once per table node the query reads.
  readonly Dictionary<TableNode, QueryNode?> _filters = new(ReferenceEqualityComparer.Instance);
  int _aliasCount;

  /// <summary>Builds the SQL tree for a prepared tree: a select of rows; for a query that is one value, the select
  /// with the aggregate on its list or the <c>EXISTS</c> over it; for a query run as a mutation, the statement.
  /// </summary>
  /// <param name="sqlTierTop">The highest node on the Sql tier; whatever is PostSql above it runs in C# over what
  /// this returns.</param>
  /// <remarks>Every operation is built by building its source, then converting the operation's lambdas with each
  /// parameter standing for a row of that rowset. One select absorbs what it can; where the chain goes backwards,
  /// the select so far becomes a derived table and a new select opens over it. Output lists are empty at this
  /// point, filled on demand.</remarks>
  private QueryNode TransformToSqlTree(QueryNode sqlTierTop) {
    switch (_translatedQuery.StatementKind) {
      case StatementKind.Delete:
        return BuildDelete((MethodCallNode)sqlTierTop);
      case StatementKind.Update:
        return BuildUpdate((MethodCallNode)sqlTierTop);
      case StatementKind.Insert:
        return BuildInsert((MethodCallNode)sqlTierTop);
    }
    if (sqlTierTop is MethodCallNode root && root.IsAggregateCall())
      return BuildAggregate(root);
    // A bare entity set, or a common table expression with nothing written over it, is still a select.
    var rowSet = BuildRowSet(sqlTierTop);
    return rowSet is TableNode or CteRefNode or JsonRowsNode or TableFunctionNode
      ? OpenSelect(rowSet, SelectStage.Filtering) : rowSet;
  }

  /// <summary>Opens a new select over a source at a stage, with the table's own filter in its WHERE where the source
  /// is a table an extension filters.</summary>
  private SelectNode OpenSelect(RowSetNode source, SelectStage stage) {
    var select = new SelectNode(source, source.Entity) { Stage = stage };
    if (source is TableNode table && GetExtensionFilter(table) is { } filter)
      select.Where.AddChild(filter);
    return select;
  }

  /// <summary>Gets the condition an extension attaches to every read of a table, as the table's filter template
  /// instantiated over this table node's row; or <see langword="null"/> where the table has none. Instantiated once
  /// per table node.</summary>
  private QueryNode? GetExtensionFilter(TableNode table) {
    if (_filters.TryGetValue(table, out var known))
      return known;
    QueryNode? filter = null;
    foreach (var tableFilter in table.Table.Filters) {
      if (IsFilterIgnored(tableFilter.Owner))
        continue;
      var condition = tableFilter.Template.Instantiate(QueryTreeTemplate.RowPlaceholder, table.Row);
      filter = filter == null ? condition : TranslationHelper.MakeAnd(filter, condition);
    }
    _filters[table] = filter;
    return filter;
  }

  /// <summary>Adds the joined table's filter to a join condition, where the table has one, inside the ON so that an
  /// outer join still keeps the rows with nothing on the far side.</summary>
  private QueryNode AddFilterToJoin(TableNode table, QueryNode condition) =>
    GetExtensionFilter(table) is { } filter ? TranslationHelper.MakeAnd(condition, filter) : condition;

  // ----- rowsets ---------------------------------------------------------------------------------------------------------

  /// <summary>Builds the rowset a source produces: a table for an entity set, a select for an operation over one.
  /// </summary>
  private RowSetNode BuildRowSet(QueryNode node) {
    switch (node) {
      case EntitySetNode set:
        return BuildTable(set.Entity);

      // The rows of a parameter stand in the SQL tree as they stood in the prepared one, with an alias.
      case JsonRowsNode rows:
        rows.Alias ??= MakeAlias();
        return rows;

      // A function's rows likewise: the call is the source, its arguments written into it.
      case TableFunctionNode function:
        function.Alias ??= MakeAlias();
        return function;

      // A JSON array of objects read off a row, transformed already since the walk is bottom-up, is its elements.
      case ColumnNode or NamedOpNode when node.DataType is { } documentType && JsonTranslationHelper.GetJsonElementType(documentType) is { } element:
        return BuildJsonRows(node, element);

      case MethodCallNode call
          when call.IsQueryableCall():
        return BuildQueryableCall(call);

      case MethodCallNode cte when TranslationHelper.IsCteCall(cte):
        return BuildCte(cte);

      case MethodCallNode function when TranslationHelper.IsTableFunctionCall(function):
        return BuildTableFunctionCall(function);

      // The rows found so far, inside a recursive query's step: a read of the CTE being defined.
      case LambdaParameterNode found when _cteSelf.TryGetValue(found, out var self): {
        var reference = MakeCteRef(self);
        self.SelfReferences.Add(reference);
        return reference;
      }

      // Built already: the source of a CTE written inside a lambda, which the bottom-up walk reached first.
      case SelectNode or TableNode or DerivedRowSetNode or CteRefNode or SetOperationNode:
        return (RowSetNode)node;

      default:
        throw new NotSupportedException($"A '{node}' node produces no rows a select can read from.");
    }
  }

  /// <summary>Builds the rows of a function written inside the query as <c>TableFunctions.Call</c>: the call over the
  /// function's table, its arguments as they were transformed, values of the outer row among them.</summary>
  private TableFunctionNode BuildTableFunctionCall(MethodCallNode call) {
    var entity = call.Entity
      ?? throw new InvalidOperationException($"'{call}' was not bound to the entity of a function's rows.");
    var table = _model.FindTable(entity)
      ?? throw new NotSupportedException($"'{entity.Name}' has no function in this database.");
    var arguments = new QueryNode[call.ArgumentCount];
    for (var i = 0; i < arguments.Length; i++)
      arguments[i] = call.Argument(i);
    return new TableFunctionNode(table, arguments) { Alias = MakeAlias() };
  }

  /// <summary>Builds the rows of a JSON array read off a row, correlated to it, over the element type's shape.
  /// </summary>
  private JsonRowsNode BuildJsonRows(QueryNode document, Type element) =>
    new(document, element, JsonTranslationHelper.GetJsonShape(_model, element)) {
      Alias = MakeAlias(), IsScalar = element.IsScalarType(),
    };

  /// <summary>Joins the rows of a document beside the row they are read off, as a cross join, which the builder
  /// writes as the server wants a source that reads the row it stands beside.</summary>
  private static RowNode JoinJsonRows(SelectNode select, JsonRowsNode rows) {
    rows.OwnerSelect = select;
    select.From.AddChild(new JoinNode(rows, QueryMaker.Constant(true), JoinType.Cross));
    return rows.Row;
  }

  private TableNode BuildTable(ILinqEntity entity) {
    var table = _model.FindTable(entity)
      ?? throw new NotSupportedException($"Entity '{entity.Name}' is stored in no table of this database.");
    return new TableNode(table) { Alias = MakeAlias() };
  }

  private string MakeAlias() => "t" + _aliasCount++;
}
