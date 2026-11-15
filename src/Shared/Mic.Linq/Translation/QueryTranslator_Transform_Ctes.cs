using System.Linq.Expressions;
using Mic.Common;
using Mic.Linq.Model;
using Mic.Linq.Nodes;

namespace Mic.Linq.Translation;

// ----- phase 2: the common table expressions --------------------------------------------------------------------------

public sealed partial class QueryTranslator {
  // The statement's WITH list, in the order the definitions were completed, which is dependency order; and by name,
  // since the name makes two uses of one local one definition.
  readonly List<CteNode> _cteList = [];
  readonly Dictionary<string, CteNode> _ctes = new(StringComparer.Ordinal);
  // What a recursive query's step parameter stands for while the step is being built: the CTE being defined.
  readonly Dictionary<LambdaParameterNode, CteNode> _cteSelf = new(ReferenceEqualityComparer.Instance);

  /// <summary>Specifies the name of the level column of a recursive query, which is zero in the anchor and one more
  /// each step.</summary>
  public const string CteLevelColumn = "__lvl";

  private CteRefNode MakeCteRef(CteNode cte) => new(cte) { Alias = MakeAlias() };

  /// <summary>Builds a common table expression, defined once per name, and returns a read of it.</summary>
  private RowSetNode BuildCte(MethodCallNode call) {
    var name = (call.Argument(1) as ConstantNode)?.Value as string
      ?? throw new NotSupportedException(
        $"The name of a common table expression is part of the statement's text — write it as a literal in {call.MethodName}.");
    return TranslationHelper.IsAsCte(call) ? BuildNamedCte(call, name) : BuildRecursiveCte(call, name);
  }

  /// <summary>Builds a named common table expression, the query's select as the body, read by name.</summary>
  /// <remarks>A local read twice is converted twice, so the second definition is built, found to be the first one
  /// again by its shape, and dropped; a different query under the same name is refused.</remarks>
  private RowSetNode BuildNamedCte(MethodCallNode call, string name) {
    var kind = (call.Argument(2) as ConstantNode)?.Value is CteKind said ? said : CteKind.Default;
    var body = BuildRowSet(call.Argument(0));
    if (body is TableNode or CteRefNode or DerivedRowSetNode)
      body = OpenSelect(body, SelectStage.Filtering);
    if (body is SelectNode select) {
      if (_bundled.Contains(select))
        throw new NotSupportedException(
          $"'{name}': a projection that hands whole rows on cannot be a common table expression — select the values you need.");
      if (!select.OrderBy.IsEmpty && select.Skip.IsEmpty && select.Take.IsEmpty)
        throw new NotSupportedException(
          $"'{name}': an ordering inside a common table expression decides nothing — order the query that reads it, "
          + "or take a page of the ordered one.");
    }
    RefuseTiedToARow(body, name);
    return MakeCteRef(RegisterCte(new CteNode(name, body, kind)));
  }

  /// <summary>Builds a recursive common table expression: the anchor and the recursive part as one <c>UNION ALL</c>,
  /// each carrying the level, which bounds the recursion on every server alike.</summary>
  /// <remarks>The columns are fixed before the recursive part is built, since that part reads the CTE while it is
  /// being defined: the level, then every stored member of the rows. The anchor answers with whole rows of an
  /// entity; a projected anchor is refused.</remarks>
  private RowSetNode BuildRecursiveCte(MethodCallNode call, string name) {
    var stepLambda = call.GetLambda(2);
    var result = call.Argument(3) as LambdaNode;
    var maxDepth = call.Argument(call.ArgumentCount - 1) is ConstantNode { Value: int depth } ? depth
      : throw new NotSupportedException($"'{name}': the depth of a recursive query is part of its text — write it as a literal.");
    if (maxDepth < 1)
      throw new NotSupportedException($"'{name}': a recursive query has to be allowed at least one step.");

    var anchor = EnsureSelect(BuildRowSet(call.Argument(0)));
    if (!anchor.OrderBy.IsEmpty && anchor.Skip.IsEmpty && anchor.Take.IsEmpty)
      throw new NotSupportedException($"'{name}': an ordering on the anchor of a recursive query decides nothing.");
    // A paged anchor is cut off first: a LIMIT on one side of a UNION ALL would be read as the whole statement's.
    if (!anchor.Skip.IsEmpty || !anchor.Take.IsEmpty)
      anchor = OpenSelect(new DerivedRowSetNode(anchor) { Alias = MakeAlias() }, SelectStage.Filtering);
    var entity = anchor.Entity;
    if (entity == null || !IsStored(entity) || !anchor.SelectList.IsEmpty || anchor.IsGrouped || anchor.IsDistinct
        || _bundled.Contains(anchor))
      throw new NotSupportedException(
        $"'{name}': a recursive query answers with whole rows of an entity — its anchor cannot project, group or "
        + "deduplicate. Select from the result instead.");
    RefuseTiedToARow(anchor, name);

    var members = GetResultMembers(entity, anchor.ResultSet);
    var level = new SelectedValueNode(QueryMaker.Constant(0), CteLevelColumn);
    anchor.SelectList.AddChild(level);
    foreach (var member in members)
      ProjectMember(anchor, member);
    var cte = new CteNode(name, anchor, CteKind.Default) { Columns = [.. anchor.SelectList.Children.Cast<SelectedValueNode>()] };

    // The recursive part, with the step parameter standing for the CTE itself.
    _cteSelf[(LambdaParameterNode)stepLambda.Children[0]] = cte;
    var step = EnsureSelect(BuildRowSet(stepLambda.Body));
    if (cte.SelfReferences.Count != 1 || !ReadsRowSetInFrom(step, cte.SelfReferences[0]))
      throw new NotSupportedException(
        $"'{name}': the recursive part has to read the rows found so far exactly ONCE, in its own FROM — not twice, "
        + "and not from inside a sub-query or a derived table. No server accepts any other form.");
    if (!step.SelectList.IsEmpty || step.IsGrouped || step.IsDistinct || !step.OrderBy.IsEmpty || !step.Skip.IsEmpty
        || !step.Take.IsEmpty || !ReferenceEquals(step.Entity, entity))
      throw new NotSupportedException(
        $"'{name}': the recursive part has to answer with whole rows of '{entity.Name}', as the anchor does, and "
        + "cannot order, page, group or project.");
    // The rows the step answers with are joined inner whatever the model says: a left join through a nullable
    // reference would answer a row of nulls where the walk ends, and two servers refuse an outer join in a recursive
    // part.
    foreach (var source in step.From.Children)
      if (source is JoinNode { JoinType: JoinType.LeftOuter } reached && ReferenceEquals(reached.Target, step.ResultSet)) {
        reached.JoinType = JoinType.Inner;
        _outerJoined.Remove(reached.Target);
      }
    var foundRow = cte.SelfReferences[0].Row;
    var foundLevel = new ColumnNode(foundRow, level);
    step.SelectList.AddChild(new SelectedValueNode(
      TranslationHelper.MakeBinary(ExpressionType.Add, foundLevel, QueryMaker.Constant(1)), CteLevelColumn));
    foreach (var member in members)
      ProjectMember(step, member);
    step.Where.AddChild(TranslationHelper.MakeBinary(ExpressionType.LessThan, new ColumnNode(foundRow, level), QueryMaker.Constant(maxDepth)));
    step.Stage = SelectStage.Projecting;
    anchor.Stage = SelectStage.Projecting;
    cte.ReplaceBody(new SetOperationNode(SetOperationType.Concat, anchor, step, entity));

    var reference = MakeCteRef(RegisterCte(cte));
    if (result == null)
      return reference;
    // The level handed to the caller: a projection over the row and the level column, like any other.
    var select = OpenSelect(reference, SelectStage.Filtering);
    return BuildProjection(select, result, call.Entity, reference.Row, new ColumnNode(reference.Row, reference.Cte.Columns![0]));
  }

  /// <summary>Registers a definition under its name, or returns the one already there where this is the same query
  /// defined again.</summary>
  private CteNode RegisterCte(CteNode cte) {
    cte.ShapeKey = cte.Body.GetCacheKey();
    if (_ctes.TryGetValue(cte.CteName, out var known)) {
      if (known.ShapeKey != cte.ShapeKey || known.CteKind != cte.CteKind)
        throw new NotSupportedException(
          $"'{cte.CteName}' names two different queries in one statement — a common table expression's name is its "
          + "identity. Give the second another name.");
      return known;
    }
    _ctes[cte.CteName] = cte;
    _cteList.Add(cte);
    return cte;
  }

  /// <summary>Determines whether a select reads a rowset in its own FROM, as its source or joined.</summary>
  private static bool ReadsRowSetInFrom(SelectNode select, RowSetNode rowSet) {
    foreach (var source in select.From.Children)
      if (ReferenceEquals(source is JoinNode joined ? joined.Target : source, rowSet))
        return true;
    return false;
  }

  /// <summary>Refuses a body that reads a row of the query around it, since a <c>WITH</c> belongs to the statement
  /// and sees no row of it.</summary>
  private static void RefuseTiedToARow(RowSetNode body, string name) {
    var own = new HashSet<RowSetNode>(ReferenceEqualityComparer.Instance);
    body.ForEach(node => {
      if (node is RowSetNode rowSet)
        own.Add(rowSet);
    });
    body.ForEach(node => {
      if (node is ColumnNode column && !own.Contains(column.Row.RowSet))
        throw new NotSupportedException(
          $"'{name}' reads a row of the query around it. A common table expression belongs to the whole statement "
          + "and sees no row of it — that is a sub-query, so write it without AsCte.");
    });
  }

  /// <summary>Gets the conditions of a WHERE with every <c>AND</c> opened up: the terms that each have to hold.
  /// </summary>
  private static IEnumerable<QueryNode> GetConjuncts(IEnumerable<QueryNode> conditions) {
    foreach (var condition in conditions)
      if (condition is LogicalOpNode { OpType: LogicalOpType.And } and)
        foreach (var term in GetConjuncts(and.Children))
          yield return term;
      else
        yield return condition;
  }

  /// <summary>Determines whether a value reads a column of the given rowsets, or with <paramref name="inside"/>
  /// false a column of any other rowset.</summary>
  /// <remarks>A select nested in the value is not looked into: its columns are its own tables', neither side's.
  /// </remarks>
  private static bool ReadsColumnFrom(QueryNode node, HashSet<RowSetNode> rowSets, bool inside) {
    if (node is ColumnNode column)
      return rowSets.Contains(column.Row.RowSet) == inside;
    if (node is RowNode row)
      return rowSets.Contains(row.RowSet) == inside;
    if (node is SelectNode)
      return false;
    foreach (var child in node.Children)
      if (ReadsColumnFrom(child, rowSets, inside))
        return true;
    return false;
  }

  private bool ReadsOuterJoined(QueryNode node) {
    if (node is ColumnNode column && _outerJoined.Contains(column.Row.RowSet))
      return true;
    foreach (var child in node.Children)
      if (ReadsOuterJoined(child))
        return true;
    return false;
  }
}
