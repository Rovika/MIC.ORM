using System.Linq.Expressions;
using Mic.Common;
using Mic.Linq.Model;
using Mic.Linq.Nodes;

namespace Mic.Linq.Translation;

// ----- phase 2: the values ---------------------------------------------------------------------------------------------

public sealed partial class QueryTranslator {
  /// <summary>Builds a lambda's body as a condition over the row its parameter stands for.</summary>
  private QueryNode BuildCondition(LambdaNode lambda, RowNode row) => BuildValue(lambda, row);

  /// <summary>Builds a lambda's body as a value over the rows its parameters stand for: a parameter becomes the row,
  /// a member read off it a column, and the logical operators the engine's own.</summary>
  private QueryNode BuildValue(LambdaNode lambda, params QueryNode[] rows) => BuildBodyValue(lambda, lambda.Body, rows);

  /// <summary>Builds a part of a lambda's body, such as what is under a <c>DefaultIfEmpty()</c> a spread takes off,
  /// as a value over the rows its parameters stand for.</summary>
  /// <remarks>A parameter standing for a bundle is bound to nothing: a member named off it is read as the slot's
  /// built value.</remarks>
  private QueryNode BuildBodyValue(LambdaNode lambda, QueryNode body, params QueryNode[] rows) {
    if (lambda.ParameterCount != rows.Length)
      throw new NotSupportedException(
        $"A lambda with {lambda.ParameterCount} parameter(s) is written where {rows.Length} row(s) are in scope.");
    for (var i = 0; i < rows.Length; i++) {
      var parameter = (LambdaParameterNode)lambda.Children[i];
      if (parameter.Entity is BundleShape)
        continue;
      // A row of a one-column projection is the column: the parameter stands for the derived table's one output.
      _rows[parameter] = parameter.Entity == null
          && rows[i] is RowNode { RowSet: DerivedRowSetNode { Select.SelectList: { ChildCount: 1 } list } } valueRow
        ? new ColumnNode(valueRow, (SelectedValueNode)list.Children[0])
        // A row of a JSON array of scalars is the element.
        : parameter.Entity == null && rows[i] is RowNode { RowSet: JsonRowsNode { IsScalar: true } scalars } elementRow
        ? new ColumnNode(elementRow, scalars.Columns[0])
        : rows[i];
    }
    return body.Transform(TransformValue, bottomUp: true);
  }

  private QueryNode TransformValue(QueryNode node) => node switch {
    // A parameter not yet bound belongs to a lambda deeper in, whose operation binds it when the walk reaches it.
    LambdaParameterNode parameter => _rows.TryGetValue(parameter, out var row) ? row : parameter,

    // A member of a bundle is the slot's built value, read in place.
    MemberAccessNode member when member.Target?.GetEntity() is BundleShape bundle =>
      _bundles.TryGetValue(bundle, out var slots)
        ? slots[bundle.IndexOf(member.MemberName)]
        : throw new InvalidOperationException($"The projection '{bundle.Name}' was named before it was built."),

    // On a row of a group, g.Key is the one key's expression, or the group row itself for a new { … }.
    MemberAccessNode { Target: RowNode { RowSet: SelectNode { IsGrouped: true } grouped } } member
        when member.MemberName == "Key" && member.ModelMember == null =>
      (grouped.Entity is GroupShape shape ? shape.IsComposite : grouped.Keys.Count > 1)
        ? member.Target
        : grouped.Keys[0].Source,
    // A member of the source row, which binding gave the grouped select's own source member, is read off the
    // source.
    MemberAccessNode { Target: RowNode { RowSet: SelectNode { IsGrouped: true } grouped }, ModelMember: { } owned } member
        when owned.Entity is not { IsTransient: true } =>
      TransformValue(new MemberAccessNode(member.MemberName, member.ClrMember, member.DataType) {
        Kind = NodeKind.Member, ModelMember = owned, Children = { grouped.ResultSet.Row },
      }),
    // A key by name.
    MemberAccessNode { Target: RowNode { RowSet: SelectNode { IsGrouped: true } grouped } } member =>
      FindGroupKey(grouped, member.MemberName)
        ?? throw new NotSupportedException(
          $"'{member.MemberName}' is not a key of the grouping, and a grouped select can only name its keys and aggregates over the group."),
    MethodCallNode call
        when call.IsQueryableCall()
          && call.Argument(0) is RowNode { RowSet: SelectNode { IsGrouped: true } grouped } =>
      BuildGroupAggregate(call, grouped),

    // A reference read off a row is a row of the table it reaches, joined in; a list is a correlated sub-select; a
    // value member is a column.
    MemberAccessNode { ModelMember: { Kind: LinqMemberKind.Reference } reference, Target: RowNode row } =>
      JoinReferencedTable(row, reference).Row,
    MemberAccessNode { ModelMember: { Kind: LinqMemberKind.List } list, Target: RowNode row } =>
      BuildListSelect(row, list),
    MemberAccessNode { ModelMember: { } member, Target: RowNode row } => BuildColumn(row, member),

    // An operation over a sub-select, or the first operation of an uncorrelated sub-query over a captured entity set.
    MethodCallNode call
        when call.IsQueryableCall() && call.Argument(0) is SelectNode sub =>
      BuildSubQueryCall(call, sub),
    MethodCallNode call
        when call.IsQueryableCall() && call.Argument(0) is EntitySetNode entitySet =>
      call.IsAggregateCall() ? BuildAggregate(call)
        : call.MethodName == nameof(Enumerable.Contains) ? BuildSubQueryCall(call, OpenSelect(BuildTable(entitySet.Entity), SelectStage.Filtering))
        : BuildQueryableCall(call),
    // The rows of a parameter, or of a JSON array read off the row, as a sub-query's source.
    MethodCallNode call
        when call.IsQueryableCall()
          && (call.Argument(0) is JsonRowsNode
            || call.Argument(0) is ColumnNode or NamedOpNode
              && call.Argument(0).DataType is { } documentType && JsonTranslationHelper.GetJsonElementType(documentType) != null) =>
      call.IsAggregateCall() ? BuildAggregate(call) : BuildQueryableCall(call),

    // A common table expression written inside a lambda is defined like any other and read as a sub-select.
    MethodCallNode cte when TranslationHelper.IsCteCall(cte) => EnsureSelect(BuildCte(cte)),

    // A function's rows written inside a lambda: the call, which a spread joins in.
    MethodCallNode function when TranslationHelper.IsTableFunctionCall(function) => BuildTableFunctionCall(function),

    // list.Contains(value) over a collection the caller brought is value IN list; the SQL builder renders the
    // collection when the statement runs.
    MethodCallNode { MethodName: nameof(Enumerable.Contains) } contains
        when contains.IsQueryableCall()
          && (contains.Argument(0) is QueryParameterNode
            || contains.Argument(0) is ConstantNode literal && literal.Flags.HasFlag(NodeFlags.List)) =>
      QueryMaker.NamedOp(NamedOpType.In, typeof(bool), contains.Argument(1), contains.Argument(0)),

    // value IN (query) by name: the query has to produce one column.
    NamedOpNode { OpType: NamedOpType.In } inQuery when inQuery.Children[1] is EntitySetNode or SelectNode =>
      RequireOneColumn(inQuery),

    // A row tested for null, as the row of an outer join is, is tested by its key.
    NamedOpNode { OpType: NamedOpType.IsNull or NamedOpType.IsNotNull } test when test.Children[0] is RowNode tested =>
      BuildRowNullTest(tested, test.OpType),
    // Two rows of the query compared are compared by their keys.
    BinaryNode { NodeType: ExpressionType.Equal or ExpressionType.NotEqual, Left: RowNode left, Right: RowNode right }
        rows =>
      BuildRowComparison(left, right, negated: rows.NodeType == ExpressionType.NotEqual),
    BinaryNode { NodeType: ExpressionType.AndAlso } and => TranslationHelper.MakeAnd(and.Left, and.Right),
    BinaryNode { NodeType: ExpressionType.OrElse } or => TranslationHelper.MakeOr(or.Left, or.Right),
    ExpressionNode { NodeType: ExpressionType.Not } not => TranslationHelper.MakeNot(not.Children[0]),
    // A conversion, such as an enum compared as its integer or a nullable lifted, is the compiler's; the engine
    // compares the column as it is stored.
    ExpressionNode { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert => convert.Children[0],
    _ => node,
  };

  /// <summary>Puts the query's select in place in a <c>value IN (query)</c>, refusing one that does not produce one
  /// column.</summary>
  private QueryNode RequireOneColumn(NamedOpNode inQuery) {
    var list = inQuery.Children[1] is EntitySetNode entitySet ? OpenSelect(BuildTable(entitySet.Entity), SelectStage.Filtering) : (SelectNode)inQuery.Children[1];
    if (list.SelectList.ChildCount != 1)
      throw new NotSupportedException(
        "A query used as a list has to produce ONE column — project the value you mean with .Select(…).");
    inQuery.Children[1] = list;
    return inQuery;
  }

  /// <summary>Finds the key with a name on a grouped select: the expression the grouping groups on, or
  /// <see langword="null"/> if there is no key of that name.</summary>
  private static QueryNode? FindGroupKey(SelectNode grouped, string name) {
    foreach (var key in grouped.Keys)
      if (string.Equals(key.Name, name, StringComparison.OrdinalIgnoreCase))
        return key.Source;
    return null;
  }

  /// <summary>Builds an aggregate over a group's source rows: the selector, or the grouping's element selector where
  /// none was written, is converted against the source row.</summary>
  /// <remarks>A selector on top of an element selector is refused, since it binds its parameter to the element and
  /// not to a row.</remarks>
  private QueryNode BuildGroupAggregate(MethodCallNode call, SelectNode grouped) {
    var row = grouped.ResultSet.Row;
    var count = call.ArgumentCount;
    switch (call.MethodName) {
      case nameof(Enumerable.Count):
      case nameof(Enumerable.LongCount):
        if (count > 1)
          throw new NotSupportedException("A counted group takes no predicate — filter the rows before grouping, or the groups after.");
        return new AggregateValueNode(AggregateType.Count, null, call.DataType);
      case nameof(Enumerable.Sum):
      case nameof(Enumerable.Min):
      case nameof(Enumerable.Max):
      case nameof(Enumerable.Average): {
        if (count > 1 && grouped.GroupElement != null)
          throw new NotSupportedException(
            "an aggregate over a grouping's ELEMENTS takes no selector — the GroupBy's element selector already said "
            + "what to aggregate, so say it there or drop the element selector");
        var selector = count > 1 ? call.GetLambda(1) : grouped.GroupElement
          ?? throw new NotSupportedException($"{call.MethodName} over a group needs a selector — {call.MethodName}(x => …).");
        var opType = call.MethodName switch {
          nameof(Enumerable.Sum) => AggregateType.Sum,
          nameof(Enumerable.Min) => AggregateType.Min,
          nameof(Enumerable.Max) => AggregateType.Max,
          _ => AggregateType.Average,
        };
        return new AggregateValueNode(opType, BuildValue(selector, row), call.DataType);
      }
      default:
        throw new NotSupportedException($"'{call.MethodName}' over a group is not something a select can say.");
    }
  }

  /// <summary>Builds a member of a row as a column: the mapping's column for a row of a table; for a row of a derived
  /// table, the inner select's output value for that member, projected out on demand.</summary>
  private ColumnNode BuildColumn(RowNode row, ILinqEntityMember member) {
    var entity = row.Entity
      ?? throw new InvalidOperationException($"A row of '{row.RowSet}' has no entity to look '{member.Name}' up on.");
    if (row.RowSet is DerivedRowSetNode derived)
      return new ColumnNode(row, ProjectMember(derived.Select, member));
    // A row of a parameter's rows names its columns itself.
    if (row.RowSet is JsonRowsNode)
      return new ColumnNode(row, member);
    // A recursive common table expression fixed its columns when its anchor was built; any other is read like a
    // derived table, or like a set operation where its body is two selects combined.
    if (row.RowSet is CteRefNode { Cte: var cte }) {
      if (cte.Columns != null)
        return new ColumnNode(row, cte.Columns.FirstOrDefault(c => c.Name == member.Name)
          ?? throw new NotSupportedException(
            $"'{entity.Name}.{member.Name}' is not a column of the recursive query '{cte.CteName}'."));
      if (cte.Body is SetOperationNode both) {
        ProjectMember((SelectNode)both.Right, member);
        return new ColumnNode(row, ProjectMember((SelectNode)both.Left, member));
      }
      return new ColumnNode(row, ProjectMember((SelectNode)cte.Body, member));
    }
    // A row of a set operation is a row of both sides: the member is projected out of each, and the column points at
    // the left's.
    if (row.RowSet is SetOperationNode combined) {
      ProjectMember((SelectNode)combined.Right, member);
      return new ColumnNode(row, ProjectMember((SelectNode)combined.Left, member));
    }
    var column = _model.FindColumn(entity, member)
      ?? throw new NotSupportedException(
        $"'{entity.Name}.{member.Name}' is stored in no column of this database, so it cannot stand in a statement.");
    return new ColumnNode(row, column);
  }

  /// <summary>Gets the output value of a select for a member of its rows: the one it already selects under that
  /// name, or a new one over the member's column of its result row.</summary>
  private SelectedValueNode ProjectMember(SelectNode select, ILinqEntityMember member) {
    foreach (var existing in select.SelectList.Children)
      if (existing.Name == member.Name)
        return (SelectedValueNode)existing;
    // Off the rows the select answers with, which are its source's unless a join moved the result.
    var selected = new SelectedValueNode(BuildColumn(select.ResultSet.Row, member), member.Name);
    select.SelectList.AddChild(selected);
    return selected;
  }
}
