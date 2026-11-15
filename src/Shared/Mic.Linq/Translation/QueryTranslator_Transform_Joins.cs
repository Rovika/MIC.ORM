using System.Linq.Expressions;
using Mic.Common;
using Mic.Linq.Model;
using Mic.Linq.Nodes;

namespace Mic.Linq.Translation;

// ----- phase 2: the joins and sub-selects ------------------------------------------------------------------------------

public sealed partial class QueryTranslator {
  readonly Dictionary<(RowNode Owner, ILinqEntityMember Member), TableNode> _joins = new();
  readonly HashSet<RowSetNode> _outerJoined = new(ReferenceEqualityComparer.Instance);

  /// <summary>Gets the table a reference reaches from a row, joined into the row's select the first time it is asked
  /// for, one join per path.</summary>
  /// <remarks>Outerness is inherited: once anything in a chain of references is optional, everything reached through
  /// it is joined outer, since an inner join off an outer-joined table would drop every row where that table was
  /// absent.</remarks>
  private TableNode JoinReferencedTable(RowNode owner, ILinqEntityMember reference) {
    if (_joins.TryGetValue((owner, reference), out var joined))
      return joined;
    var info = reference.ReferenceInfo
      ?? throw new InvalidOperationException($"Reference '{reference.Name}' names no target entity.");
    if (info.FromMembers.Count == 0)
      throw new NotSupportedException($"Reference '{reference.Name}' has no key behind it, so there is nothing to join on.");
    var select = owner.RowSet.OwnerSelect
      ?? throw new InvalidOperationException($"A row of '{owner.RowSet}' belongs to no select to join '{reference.Name}' into.");

    var table = BuildTable(info.TargetEntity);
    var outer = reference.IsNullable || _outerJoined.Contains(owner.RowSet);
    table.OwnerSelect = select;
    select.From.AddChild(new JoinNode(table,
      AddFilterToJoin(table, BuildKeyPairing(owner, info.FromMembers, table.Row, info.ToMembers)),
      outer ? JoinType.LeftOuter : JoinType.Inner, reference));
    if (outer)
      _outerJoined.Add(table);
    _joins[(owner, reference)] = table;
    return table;
  }

  /// <summary>Builds the key pairing two rows are joined or correlated on, one equality per key member joined by
  /// <c>AND</c>.</summary>
  private QueryNode BuildKeyPairing(RowNode a, IReadOnlyList<ILinqEntityMember> fromMembers, RowNode b,
      IReadOnlyList<ILinqEntityMember> toMembers) {
    var terms = new QueryNode[fromMembers.Count];
    for (var i = 0; i < terms.Length; i++)
      terms[i] = TranslationHelper.MakeBinary(ExpressionType.Equal, BuildColumn(a, fromMembers[i]), BuildColumn(b, toMembers[i]));
    return terms.Length == 1 ? terms[0] : TranslationHelper.MakeAnd(terms);
  }

  /// <summary>Gets the members that identify a row of an entity, for a comparison, a join or a grouping written
  /// over the row itself.</summary>
  /// <param name="entity">The entity of the row.</param>
  /// <param name="use">What the query does with the row, as the verb the refusal says: compare, join, group.</param>
  /// <returns>The key members.</returns>
  /// <exception cref="NotSupportedException">The entity has no primary key, as a view's or a function's rows may
  /// not, and a projection's do not.</exception>
  private static IReadOnlyList<ILinqEntityMember> GetKeyMembers(ILinqEntity entity, string use) =>
    entity.PrimaryKeyMembers is { Count: > 0 } key
      ? key
      : throw new NotSupportedException(
        $"'{entity.Name}' has no primary key to {use} its rows by; name the members to {use} by instead of the row.");

  private static ILinqEntity GetEntity(RowNode row) =>
    row.Entity ?? throw new NotSupportedException("A compared row names no entity.");

  /// <summary>Builds the comparison of two rows of a query as the comparison of their keys.</summary>
  /// <param name="left">One row.</param>
  /// <param name="right">The other row.</param>
  /// <param name="negated"><see langword="true"/> for <c>!=</c>.</param>
  private QueryNode BuildRowComparison(RowNode left, RowNode right, bool negated) {
    var leftKeys = GetKeyMembers(GetEntity(left), "compare");
    var rightKeys = GetKeyMembers(GetEntity(right), "compare");
    if (leftKeys.Count != rightKeys.Count)
      throw new NotSupportedException("Two rows compared have primary keys of different widths.");
    var pairing = BuildKeyPairing(left, leftKeys, right, rightKeys);
    return negated ? TranslationHelper.MakeNot(pairing) : pairing;
  }

  /// <summary>Builds the test of a row for null, which the row of an outer join is where nothing matched, as the
  /// test of each of its key columns.</summary>
  /// <param name="row">The row.</param>
  /// <param name="test"><see cref="NamedOpType.IsNull"/> or <see cref="NamedOpType.IsNotNull"/>.</param>
  private QueryNode BuildRowNullTest(RowNode row, NamedOpType test) {
    var keys = GetKeyMembers(GetEntity(row), "compare");
    var terms = new QueryNode[keys.Count];
    for (var i = 0; i < terms.Length; i++)
      terms[i] = QueryMaker.NamedOp(test, typeof(bool), BuildColumn(row, keys[i]));
    return terms.Length == 1 ? terms[0] : TranslationHelper.MakeAnd(terms);
  }

  /// <summary>Builds a list member as a correlated sub-select: the list's rows, tied to the owner row through the
  /// inverse's key pairing read the other way.</summary>
  /// <remarks>A many-to-many sources from the link, with the target one join further out, so the rows the sub-select
  /// answers with are the joined table's.</remarks>
  private SelectNode BuildListSelect(RowNode owner, ILinqEntityMember list) {
    var info = list.ListInfo
      ?? throw new NotSupportedException($"List '{list.Name}' does not say what backs it, so it cannot be correlated.");
    var target = info.TargetEntity;
    var inverse = info.InverseMember
      ?? throw new NotSupportedException(
        $"List '{list.Name}' does not say which reference points back at it, so it cannot be correlated.");
    var pairing = inverse.ReferenceInfo
      ?? throw new NotSupportedException($"List '{list.Name}': its inverse '{inverse.Name}' has no key behind it.");
    var near = inverse.Entity
      ?? throw new NotSupportedException($"List '{list.Name}' does not say which entity its inverse belongs to.");

    var source = BuildTable(near);
    var sub = OpenSelect(source, SelectStage.Filtering);
    sub.Entity = target;
    sub.OwnerSelect = owner.RowSet.OwnerSelect;
    sub.Where.AddChild(BuildKeyPairing(source.Row, pairing.FromMembers, owner, pairing.ToMembers));
    if (info.IsManyToMany) {
      var link = info.LinkTargetMember
        ?? throw new NotSupportedException(
          $"List '{list.Name}' goes through '{near.Name}' and does not say which reference leads on to '{target.Name}'.");
      sub.ResultSet = JoinReferencedTable(source.Row, link);
    }
    return sub;
  }

  /// <summary>Joins the rows a spread joins in, and returns a row of the joined side: a correlated sub-select becomes
  /// a join on what its WHERE said, its tables moved into this select's FROM; an entity set with nothing tying it to
  /// the row is a cross join; a page becomes numbered rows through <see cref="JoinPagedRows"/>.</summary>
  /// <remarks>A sub-select that projects, groups or deduplicates would need a derived table correlated to the row,
  /// which not every server has, and is refused; so is an ordering with no page. A list spread off an outer-joined
  /// row is joined outer too.</remarks>
  private RowNode JoinSpreadRows(SelectNode select, QueryNode built, bool keepEmpty) {
    switch (built) {
      case SelectNode sub: {
        if (sub.IsGrouped || sub.IsDistinct || !sub.SelectList.IsEmpty)
          throw new NotSupportedException(
            "Only a filtered or paged list or query spreads into a join — projected, grouped or distinct, it would be "
            + "a derived table correlated to the row. Filter or page it here, and project the result.");
        if (!sub.Take.IsEmpty || !sub.Skip.IsEmpty)
          return JoinPagedRows(select, sub, keepEmpty);
        if (sub.Stage < SelectStage.Filtering)
          throw new NotSupportedException(
            "An ordering on a spread list decides nothing — a join promises no order. Take a page of the ordered list, "
            + "or order the result.");
        var condition = sub.Where.ChildCount switch {
          0 => QueryMaker.Constant(true),
          1 => sub.Where.Children[0],
          _ => TranslationHelper.MakeAnd(sub.Where.Children.ToArray()),
        };
        var outer = keepEmpty || ReadsOuterJoined(condition);
        var result = sub.ResultSet.Row;
        var sources = sub.From.Children.ToList();
        sub.From.Children.Clear();
        var first = (RowSetNode)sources[0];
        select.From.AddChild(new JoinNode(first, condition, outer ? JoinType.LeftOuter : JoinType.Inner));
        first.OwnerSelect = select;
        if (outer)
          _outerJoined.Add(first);
        for (var i = 1; i < sources.Count; i++) {
          var join = (JoinNode)sources[i];
          if (outer) {
            join.JoinType = JoinType.LeftOuter;
            _outerJoined.Add(join.Target);
          }
          join.Target.OwnerSelect = select;
          select.From.AddChild(join);
        }
        return result;
      }

      case EntitySetNode set: {
        var table = BuildTable(set.Entity);
        table.OwnerSelect = select;
        // A filtered table is joined inner on its filter, since a cross join has no ON to put it in.
        select.From.AddChild(GetExtensionFilter(table) is { } filter
          ? new JoinNode(table, filter, JoinType.Inner)
          : new JoinNode(table, QueryMaker.Constant(true), JoinType.Cross));
        return table.Row;
      }

      // A JSON array of objects read off the row spreads as its rows beside the row; by name, the rows node is
      // already there.
      case ColumnNode or NamedOpNode when built.DataType is { } documentType && JsonTranslationHelper.GetJsonElementType(documentType) is { } element:
        return JoinJsonRows(select, BuildJsonRows(built, element));
      case JsonRowsNode rows:
        rows.Alias ??= MakeAlias();
        return JoinJsonRows(select, rows);

      // A function's rows spread beside the row: laterally where an argument reads the row, cross-joined otherwise.
      case TableFunctionNode function: {
        function.Alias ??= MakeAlias();
        function.OwnerSelect = select;
        var lateral = function.Arguments.Any(ReadsAnyRow);
        select.From.AddChild(new JoinNode(function, QueryMaker.Constant(true), lateral ? JoinType.Lateral : JoinType.Cross));
        return function.Row;
      }

      default:
        throw new NotSupportedException(
          $"'{built}' is not rows a spread can join in — write a list of the row, or a query held in a local variable.");
    }
  }

  /// <summary>Determines whether a node reads a value off any row, as against a parameter or a constant.</summary>
  private static bool ReadsAnyRow(QueryNode node) {
    if (node is ColumnNode or RowNode)
      return true;
    if (node is SelectNode)
      return false;
    foreach (var child in node.Children)
      if (ReadsAnyRow(child))
        return true;
    return false;
  }

  /// <summary>Joins a page per outer row as numbered rows: the sub-select loses its tie to the outer row, its ordering
  /// and its page, numbers its rows within each value of what tied it, and is joined as a derived table on that tie
  /// and on the numbers the page asked for.</summary>
  /// <remarks>The tie has to be an equality of the inner side against the outer side, since the partition stands in
  /// for it; any other tie is refused by name. What the sub-select filters on its own rows stays inside it, before
  /// the numbering, and so does a copy of what the outer select asks of the tied value alone
  /// (<see cref="CopyTiedConditions"/>). A page tied to nothing is an ordinary derived table, cross-joined. The
  /// page's order survives as the statement's trailing order, in <see cref="SelectNode.SpreadOrder"/>.</remarks>
  private RowNode JoinPagedRows(SelectNode select, SelectNode sub, bool keepEmpty) {
    var inner = new HashSet<RowSetNode>(ReferenceEqualityComparer.Instance);
    foreach (var source in sub.From.Children)
      inner.Add(source is JoinNode joined ? joined.Target : (RowSetNode)source);

    var ties = new List<(QueryNode Inner, QueryNode Outer)>();
    var own = new List<QueryNode>();
    foreach (var condition in GetConjuncts(sub.Where.Children)) {
      if (!ReadsColumnFrom(condition, inner, inside: false)) {
        own.Add(condition);
        continue;
      }
      if (condition is BinaryNode { NodeType: ExpressionType.Equal } equal) {
        var leftOuter = ReadsColumnFrom(equal.Left, inner, inside: false);
        var (innerSide, outerSide) = leftOuter ? (equal.Right, equal.Left) : (equal.Left, equal.Right);
        if (ReadsColumnFrom(innerSide, inner, inside: true) && !ReadsColumnFrom(innerSide, inner, inside: false)
            && !ReadsColumnFrom(outerSide, inner, inside: true)) {
          ties.Add((innerSide, outerSide));
          continue;
        }
      }
      throw new NotSupportedException(
        "A page of a list spreads only where the list is tied to the row by EQUALITY — the rows are numbered within "
        + "each value of the tie. Tied any other way it would be a derived table correlated to the row.");
    }

    var derived = new DerivedRowSetNode(sub) { Alias = MakeAlias(), OwnerSelect = select };
    if (ties.Count == 0) {
      select.From.AddChild(new JoinNode(derived, QueryMaker.Constant(true), keepEmpty ? JoinType.LeftOuter : JoinType.Cross));
      return derived.Row;
    }

    var skip = sub.Skip.ChildAt(0);
    var take = sub.Take.ChildAt(0);
    var number = new SelectedValueNode(new RowNumberNode(ties.Select(t => t.Inner), sub.OrderBy.Children), "__rn");
    sub.Where.Children.Clear();
    sub.Where.Children.AddRange(own);
    sub.Where.Children.AddRange(CopyTiedConditions(select, ties));
    sub.OrderBy.Children.Clear();
    sub.Skip.Children.Clear();
    sub.Take.Children.Clear();
    sub.Stage = SelectStage.Projecting;

    // The tie and the number are named on the sub-select's list, so from here on its members are projected out on
    // demand, as for any derived table.
    sub.SelectList.AddChild(number);
    var rowNumber = new ColumnNode(derived.Row, number);
    var terms = new List<QueryNode>();
    for (var i = 0; i < ties.Count; i++) {
      var key = new SelectedValueNode(ties[i].Inner, "__k" + i);
      sub.SelectList.AddChild(key);
      terms.Add(TranslationHelper.MakeBinary(ExpressionType.Equal, new ColumnNode(derived.Row, key), ties[i].Outer));
    }
    if (skip != null)
      terms.Add(TranslationHelper.MakeBinary(ExpressionType.GreaterThan, rowNumber, skip));
    if (take != null)
      terms.Add(TranslationHelper.MakeBinary(ExpressionType.LessThanOrEqual, rowNumber,
        skip == null ? take : TranslationHelper.MakeBinary(ExpressionType.Add, skip, take)));

    var outer = keepEmpty || ties.Any(t => ReadsOuterJoined(t.Outer));
    select.From.AddChild(new JoinNode(derived, TranslationHelper.MakeAnd(terms.ToArray()), outer ? JoinType.LeftOuter : JoinType.Inner));
    if (outer)
      _outerJoined.Add(derived);
    select.SpreadOrder.Add(rowNumber);
    return derived.Row;
  }


  /// <summary>Copies the conditions of a select that read nothing of its own rows but what a page is tied to, each
  /// said over the inner side of the tie, for the page's sub-select to filter by before it numbers its rows.
  /// </summary>
  /// <remarks>A row that joins has the tie's value on both sides, so such a condition holds for both rows or for
  /// neither: the copy drops only rows that no row the outer select keeps would have joined, and the numbering
  /// within the values that stay is as it was. Without the copy the server numbers every row of the inner tables
  /// and the join cuts them afterwards. A condition that reads anything else of the outer rows is left where it
  /// is.</remarks>
  private static List<QueryNode> CopyTiedConditions(SelectNode select, List<(QueryNode Inner, QueryNode Outer)> ties) {
    var copies = new List<QueryNode>();
    foreach (var condition in GetConjuncts(select.Where.Children)) {
      // The rowsets of the condition's own sub-selects, whose columns are copied with it.
      var own = new HashSet<RowSetNode>(ReferenceEqualityComparer.Instance);
      CollectRowSets(condition, own);
      var readsTie = false;
      if (ReadsTiedValuesOnly(condition, own, ties, ref readsTie) && readsTie)
        copies.Add(condition.Clone(node => FindTiedInner(node, ties)?.Clone()));
    }
    return copies;
  }

  private static void CollectRowSets(QueryNode node, HashSet<RowSetNode> rowSets) {
    if (node is RowSetNode rowSet)
      rowSets.Add(rowSet);
    if (node.HasChildren)
      foreach (var child in node.Children)
        CollectRowSets(child, rowSets);
  }

  /// <summary>Determines whether every column a condition reads is of its own sub-selects or is the outer side of a
  /// tie, noting whether a tie is read at all.</summary>
  private static bool ReadsTiedValuesOnly(QueryNode node, HashSet<RowSetNode> own,
      List<(QueryNode Inner, QueryNode Outer)> ties, ref bool readsTie) {
    switch (node) {
      case ColumnNode column when own.Contains(column.Row.RowSet):
        return true;
      case ColumnNode column:
        if (FindTiedInner(column, ties) == null)
          return false;
        readsTie = true;
        return true;
      case RowNode row:
        return own.Contains(row.RowSet);
    }
    if (node.HasChildren)
      foreach (var child in node.Children)
        if (!ReadsTiedValuesOnly(child, own, ties, ref readsTie))
          return false;
    return true;
  }

  /// <summary>Finds the inner side of the tie whose outer side is the column given, or returns
  /// <see langword="null"/> for any other node.</summary>
  private static QueryNode? FindTiedInner(QueryNode node, List<(QueryNode Inner, QueryNode Outer)> ties) {
    if (node is not ColumnNode column)
      return null;
    foreach (var (inner, outer) in ties)
      if (outer is ColumnNode tied && ReferenceEquals(tied.Row.RowSet, column.Row.RowSet)
          && ReferenceEquals(tied.Column, column.Column) && ReferenceEquals(tied.Selected, column.Selected)
          && tied.Name == column.Name)
        return inner;
    return null;
  }

  /// <summary>Builds the equality of two join keys: two rows by their primary keys, two composites part by part, two
  /// values as they are.</summary>
  private QueryNode BuildKeyEquality(QueryNode outer, QueryNode inner) {
    if (outer is RowNode a && inner is RowNode b) {
      var aKeys = GetKeyMembers(a.Entity ?? throw new NotSupportedException("A join key row names no entity."), "join");
      var bKeys = GetKeyMembers(b.Entity ?? throw new NotSupportedException("A join key row names no entity."), "join");
      if (aKeys.Count != bKeys.Count)
        throw new NotSupportedException("Two rows joined as keys have primary keys of different widths.");
      return BuildKeyPairing(a, aKeys, b, bKeys);
    }
    if (outer is NewNode compositeA && inner is NewNode compositeB) {
      if (compositeA.ChildCount != compositeB.ChildCount)
        throw new NotSupportedException("The two join keys have different numbers of parts.");
      var terms = new QueryNode[compositeA.ChildCount];
      for (var i = 0; i < terms.Length; i++)
        terms[i] = BuildKeyEquality(compositeA.Children[i], compositeB.Children[i]);
      return terms.Length == 1 ? terms[0] : TranslationHelper.MakeAnd(terms);
    }
    return TranslationHelper.MakeBinary(ExpressionType.Equal, outer, inner);
  }

  /// <summary>Gets a rowset as a select of its own, such as a side of a set operation; a table becomes a select of
  /// the whole table.</summary>
  private SelectNode EnsureSelect(RowSetNode source) => source as SelectNode ?? OpenSelect(source, SelectStage.Filtering);

  /// <summary>Builds what an operation makes of a sub-select: a narrower sub-select, an <c>EXISTS</c>, or the
  /// sub-select selecting one aggregate and standing as that value.</summary>
  private QueryNode BuildSubQueryCall(MethodCallNode call, SelectNode sub) {
    var row = GetLambdaRow(sub);
    switch (call.MethodName) {
      case nameof(Enumerable.Where):
        if (!sub.Take.IsEmpty || !sub.Skip.IsEmpty)
          throw new NotSupportedException(
            "A filter over a page of a list would be a derived table tied to the row — filter the list before paging it.");
        sub.Where.AddChild(BuildCondition(call.GetLambda(1), row));
        return sub;

      // An ordering and a page of a list are the same clauses they are over a whole query, on the list's own
      // sub-select.
      case nameof(Enumerable.OrderBy):
      case nameof(Enumerable.OrderByDescending):
      case nameof(Enumerable.ThenBy):
      case nameof(Enumerable.ThenByDescending):
      case nameof(Enumerable.Take):
      case nameof(Enumerable.Skip):
        return BuildQueryableCallOver(call, sub);

      case nameof(Enumerable.Select): {
        var projected = GetSelectAtStage(sub, SelectStage.Projecting);
        return BuildProjection(projected, call.GetLambda(1), call.Entity, GetLambdaRow(projected));
      }

      case nameof(Enumerable.Distinct):
        sub.IsDistinct = true;
        return sub;

      // subQuery.Contains(x) is x IN (sub-select), and the sub-select has to produce one column.
      case nameof(Enumerable.Contains):
        if (sub.SelectList.ChildCount != 1)
          throw new NotSupportedException(
            "A query used as a list has to produce ONE column — project the value you mean with .Select(…).");
        return QueryMaker.NamedOp(NamedOpType.In, typeof(bool), call.Argument(1), sub);

      // One value over the sub-select, exactly as over a whole query: the same cuts and the same refusals.
      case nameof(Enumerable.Any):
      case nameof(Enumerable.All):
      case nameof(Enumerable.Count):
      case nameof(Enumerable.LongCount):
      case nameof(Enumerable.Sum):
      case nameof(Enumerable.Min):
      case nameof(Enumerable.Max):
      case nameof(Enumerable.Average):
        return BuildAggregate(call, sub);

      default:
        throw new NotSupportedException($"'{call.MethodName}' over a list is not transformed into the SQL tree yet.");
    }
  }
}
