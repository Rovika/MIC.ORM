using System.Linq.Expressions;
using Mic.Common;
using Mic.Linq.Model;
using Mic.Linq.Nodes;

namespace Mic.Linq.Translation;

// ----- phase 2: the operations, each built over its source's rowset ---------------------------------------------------

public sealed partial class QueryTranslator {
  private RowSetNode BuildQueryableCall(MethodCallNode call) => BuildQueryableCallOver(call, BuildRowSet(call.Argument(0)));

  /// <summary>Builds an operation over a source already built: the chain's own, or a list's sub-select an ordering or
  /// a page is written over.</summary>
  private RowSetNode BuildQueryableCallOver(MethodCallNode call, RowSetNode source) {
    switch (call.MethodName) {
      // A filter written above a grouping is a HAVING; which of the two a Where is depends on whether the select has
      // grouped yet.
      case nameof(Queryable.Where): {
        var grouped = source is SelectNode { IsGrouped: true };
        var select = GetSelectAtStage(source, grouped ? SelectStage.Having : SelectStage.Filtering);
        (grouped ? select.Having : select.Where).AddChild(BuildCondition(call.GetLambda(1), GetLambdaRow(select)));
        return select;
      }

      // A projection replaces the select list and is outermost of its select: everything under it applies in the
      // same select.
      case nameof(Queryable.Select): {
        if (call.Entity is BundleShape bundle) {
          var select = GetBundleSelect(source);
          return BuildBundle(select, call.GetLambda(1), bundle, GetLambdaRow(select));
        }
        var projected = GetSelectAtStage(source, SelectStage.Projecting);
        return BuildProjection(projected, call.GetLambda(1), call.Entity, GetLambdaRow(projected));
      }

      // A second source for every row is a join, the pair in scope for what follows; a list wrapped in
      // DefaultIfEmpty() keeps the rows with nothing on the far side.
      case nameof(Queryable.SelectMany): {
        var select = GetSelectAtStage(source, SelectStage.Filtering);
        var row = GetLambdaRow(select);
        var collection = call.GetLambda(1);
        var body = collection.Body;
        var keepEmpty = false;
        if (body is MethodCallNode { MethodName: nameof(Enumerable.DefaultIfEmpty) } wrapped) {
          keepEmpty = true;
          body = wrapped.Argument(0);
        }
        var joined = JoinSpreadRows(select, BuildBodyValue(collection, body, row), keepEmpty);
        if (call.ArgumentCount == 3)
          return BuildSelector(select, call.GetLambda(2), call.Entity, row, joined);
        select.ResultSet = joined.RowSet;
        select.Entity = joined.Entity;
        return select;
      }

      // A join the caller wrote: the inner rowset joined on two typed key selectors or one condition by name, and the
      // result selector over the pair. A GroupJoin joins nothing yet: its second parameter is the inner rows
      // correlated to the outer one, a sub-select counted or spread by what follows.
      case nameof(Queryable.Join):
      case nameof(Queryable.GroupJoin): {
        var select = GetSelectAtStage(source, SelectStage.Filtering);
        var row = GetLambdaRow(select);
        var inner = BuildRowSet(call.Argument(1));
        if (inner is SelectNode innerSelect) {
          // An ordered inner side with nothing to cut decides nothing.
          if (!innerSelect.OrderBy.IsEmpty && innerSelect.Skip.IsEmpty && innerSelect.Take.IsEmpty)
            throw new NotSupportedException(
              "An ordering on a joined query decides nothing — a sub-select promises no order. Order the outer query, "
              + "or take a page of the ordered one.");
          inner = new DerivedRowSetNode(innerSelect) { Alias = MakeAlias() };
        }
        var typed = call.ArgumentCount == 5;
        var innerRow = inner.Row;
        var condition = typed
          ? BuildKeyEquality(BuildValue(call.GetLambda(2), row), BuildValue(call.GetLambda(3), innerRow))
          : BuildValue(call.GetLambda(2), row, innerRow);
        var result = typed ? call.GetLambda(4) : call.ArgumentCount > 3 ? call.GetLambda(3) : null;
        if (call.MethodName == nameof(Queryable.GroupJoin)) {
          var group = OpenSelect(inner, SelectStage.Filtering);
          group.OwnerSelect = select;
          group.Where.AddChild(condition);
          return BuildSelector(select, result!, call.Entity, row, group);
        }
        var outer = call.Flags.HasFlag(NodeFlags.Outer);
        inner.OwnerSelect = select;
        if (inner is TableNode innerTable)
          condition = AddFilterToJoin(innerTable, condition);
        select.From.AddChild(new JoinNode(inner, condition, outer ? JoinType.LeftOuter : JoinType.Inner));
        if (outer)
          _outerJoined.Add(inner);
        return result == null ? select : BuildSelector(select, result, call.Entity, row, innerRow);
      }

      // Two whole selects combined; an operation over the combination reads it as a derived table.
      case nameof(Queryable.Union):
      case nameof(Queryable.Concat):
      case nameof(Queryable.Intersect):
      case nameof(Queryable.Except): {
        var opType = call.MethodName switch {
          nameof(Queryable.Union) => SetOperationType.Union,
          nameof(Queryable.Concat) => SetOperationType.Concat,
          nameof(Queryable.Intersect) => SetOperationType.Intersect,
          _ => SetOperationType.Except,
        };
        var left = EnsureSelect(source);
        var right = EnsureSelect(BuildRowSet(call.Argument(1)));
        // The two sides have to line up: whole rows of one entity on both, or the same number of values.
        var alike = left.SelectList.IsEmpty && right.SelectList.IsEmpty
          ? ReferenceEquals(left.ResultSet.Entity, right.ResultSet.Entity)
          : left.SelectList.ChildCount == right.SelectList.ChildCount && !left.SelectList.IsEmpty;
        if (!alike)
          throw new NotSupportedException(
            $"{call.MethodName} over two different shapes — the two sides have to produce the same columns: whole "
            + "rows of one entity, or the same number of values.");
        return new SetOperationNode(opType, left, right, left.Entity);
      }

      // The keys go into the GROUP BY and into the select list, since a grouped row is its keys until a projection
      // over the groups says otherwise.
      case nameof(Queryable.GroupBy): {
        var select = GetSelectAtStage(source, SelectStage.Grouping);
        var keys = call.GetLambda(1);
        var row = GetLambdaRow(select);
        var body = keys.Body as NewNode;
        var names = body?.SlotNames;
        var slots = body != null ? body.Children : [keys.Body];
        var built = BuildValue(keys, row);
        var values = body != null ? built.Children : [built];
        for (var i = 0; i < values.Count; i++) {
          var name = names?[i] ?? (slots[i] as MemberAccessNode)?.MemberName ?? slots[i].Name
            ?? throw new NotSupportedException($"Key {i} of the grouping has no name to take — give it one.");
          var key = new SelectedValueNode(values[i], name);
          select.Keys.Add(key);
          select.SelectList.AddChild(key);
          // An entity as a key groups by its key columns.
          if (values[i] is RowNode keyRow)
            foreach (var member in GetKeyMembers(
                keyRow.Entity ?? throw new NotSupportedException("A key row names no entity."), "group"))
              select.GroupBy.AddChild(BuildColumn(keyRow, member));
          else
            select.GroupBy.AddChild(values[i]);
        }
        select.Entity = call.Entity;
        if (call.ArgumentCount == 3)
          select.GroupElement = call.GetLambda(2);
        return select;
      }

      // First takes one row and Single takes two, so that "more than one matched" can be told. Unordered rows are
      // ordered by the primary key, since two servers refuse a limit without an ORDER BY; Last reverses the ordering.
      case nameof(Queryable.First):
      case nameof(Queryable.FirstOrDefault):
      case nameof(Queryable.Single):
      case nameof(Queryable.SingleOrDefault):
      case nameof(Queryable.Last):
      case nameof(Queryable.LastOrDefault): {
        // The predicate is a filter of the same select, ordered or not, since filtering before or after a stable
        // ordering gives the same rows.
        if (call.ArgumentCount > 1) {
          if (source is SelectNode { IsGrouped: false } open && open.Take.IsEmpty && open.Skip.IsEmpty && open.Stage > SelectStage.Projecting)
            open.Where.AddChild(BuildCondition(call.GetLambda(1), GetLambdaRow(open)));
          else
            source = BuildQueryableCallAs(call, nameof(Queryable.Where), source);
        }
        var select = GetSelectAtStage(source, SelectStage.Paging);
        if (select.OrderBy.IsEmpty)
          OrderByKey(select);
        if (call.MethodName.StartsWith("Last", StringComparison.Ordinal))
          foreach (var term in select.OrderBy.Children)
            term.Flags ^= NodeFlags.Descending;
        select.Take.AddChild(QueryMaker.Constant(call.MethodName.StartsWith("Single", StringComparison.Ordinal) ? 2 : 1));
        select.OneRow = call.MethodName;
        return select;
      }

      // ThenBy appends a term; a second OrderBy is refused, since in LINQ it makes the last call the primary sort. A
      // caller's OrderBy over the model's default terms takes over.
      case nameof(Queryable.OrderBy):
      case nameof(Queryable.OrderByDescending):
      case nameof(Queryable.ThenBy):
      case nameof(Queryable.ThenByDescending): {
        var select = GetSelectAtStage(source, SelectStage.Ordering);
        var isDefault = call.Flags.HasFlag(NodeFlags.DefaultOrder);
        if (!select.OrderBy.IsEmpty && !call.MethodName.StartsWith("Then", StringComparison.Ordinal)) {
          if (isDefault || !select.OrderBy.Children.TrueForAll(t => t.Flags.HasFlag(NodeFlags.DefaultOrder)))
            throw new NotSupportedException(
              "A second OrderBy re-sorts the whole sequence, which reverses the terms. Write ThenBy for a secondary "
              + "ordering.");
          select.OrderBy.Children.Clear();
        }
        var term = BuildValue(call.GetLambda(1), GetLambdaRow(select));
        // A key of the grouping read as a term is the node the GROUP BY holds; the direction goes on a copy.
        if (term is not RowNode && select.Keys.Exists(key => ReferenceEquals(key.Source, term)))
          term = term.Clone();
        if (call.MethodName.EndsWith("Descending", StringComparison.Ordinal))
          term.Flags |= NodeFlags.Descending;
        if (isDefault)
          term.Flags |= NodeFlags.DefaultOrder;
        select.OrderBy.AddChild(term);
        return select;
      }

      // A page of an unordered set is refused on every vendor: two servers refuse it, and the rest promise no order.
      case nameof(Queryable.Take): {
        var select = GetSelectAtStage(source, SelectStage.Paging);
        RequireOrdered(select, call.MethodName);
        select.Take.AddChild(call.Argument(1));
        return select;
      }

      // Skip over Take means take-then-skip, which is not what OFFSET … LIMIT does; it is refused rather than made a
      // derived table, which would have no ordering of its own to page by.
      case nameof(Queryable.Skip): {
        var select = GetSelectAtStage(source, SelectStage.Paging);
        RequireOrdered(select, call.MethodName);
        if (!select.Take.IsEmpty)
          throw new NotSupportedException("Skip applied after Take is a page of a page — write .Skip(…).Take(…), which is one page.");
        select.Skip.AddChild(call.Argument(1));
        return select;
      }

      // Distinct has no stage: it changes nothing else about the select, which stays where it was.
      case nameof(Queryable.Distinct): {
        var select = source as SelectNode ?? GetSelectAtStage(source, SelectStage.Open);
        select.IsDistinct = true;
        return select;
      }

      default:
        throw new NotSupportedException($"'{call.MethodName}' is not transformed into the SQL tree yet.");
    }
  }

  /// <summary>Builds an operation's predicate as another method would apply it: <c>First(x => …)</c> is
  /// <c>Where(x => …)</c> then <c>First()</c>.</summary>
  private RowSetNode BuildQueryableCallAs(MethodCallNode call, string asMethod, RowSetNode source) {
    var stage = asMethod == nameof(Queryable.Where) && source is SelectNode { IsGrouped: true } ? SelectStage.Having : SelectStage.Filtering;
    var select = GetSelectAtStage(source, stage);
    var grouped = select.IsGrouped;
    (grouped ? select.Having : select.Where).AddChild(BuildCondition(call.GetLambda(1), GetLambdaRow(select)));
    return select;
  }

  /// <summary>Gets the row an operation's lambda is written over: a row of a group for a grouped select, a row of
  /// what the select answers with otherwise.</summary>
  private static RowNode GetLambdaRow(SelectNode select) => select.IsGrouped ? select.Row : select.ResultSet.Row;

  /// <summary>Orders a select by the primary key, for a one-row read that said no order, so that every server
  /// accepts the limit and agrees which row.</summary>
  private void OrderByKey(SelectNode select) {
    var entity = select.ResultSet.Entity
      ?? throw new NotSupportedException("One row of an unordered set of values cannot be chosen — order it first.");
    if (entity.PrimaryKeyMembers.Count == 0)
      throw new NotSupportedException($"'{entity.Name}' has no primary key to order by — order the query first.");
    foreach (var member in entity.PrimaryKeyMembers)
      select.OrderBy.AddChild(BuildColumn(select.ResultSet.Row, member));
  }

  /// <summary>Builds a result selector over the rows in scope, one or the pair a join or a spread puts there: a
  /// bundle where it hands rows on, a projection otherwise.</summary>
  private SelectNode BuildSelector(SelectNode select, LambdaNode selector, ILinqEntity? shape, params QueryNode[] rows) =>
    shape is BundleShape bundle
      ? BuildBundle(select, selector, bundle, rows)
      : BuildProjection(select, selector, shape, rows);

  /// <summary>Gets the select a bundling projection is built in: the source itself, since a renaming changes no
  /// clause and no stage, unless the rows it bundles are a projection's or a group's, which are cut off first.
  /// </summary>
  private SelectNode GetBundleSelect(RowSetNode source) =>
    source is SelectNode { Stage: > SelectStage.Projecting, IsGrouped: false } open
      ? open
      : GetSelectAtStage(source, SelectStage.Filtering);

  /// <summary>Builds a bundle: each slot built over the rows in scope and kept, to be read in place of the member
  /// that names it; the select is otherwise as it was.</summary>
  private SelectNode BuildBundle(SelectNode select, LambdaNode selector, BundleShape bundle, params QueryNode[] rows) {
    _bundles[bundle] = BuildValue(selector, rows).Children;
    _bundled.Add(select);
    select.Entity = bundle;
    return select;
  }

  /// <summary>Builds a projection: its slots become the select list, named; a whole row selected is a change of what
  /// the select answers with, not a select list.</summary>
  private SelectNode BuildProjection(SelectNode select, LambdaNode selector, ILinqEntity? shape, params QueryNode[] rows) {
    var body = selector.Body as NewNode;
    var names = body?.SlotNames;
    var slots = body != null ? body.Children : [selector.Body];
    var built = BuildValue(selector, rows);
    var values = body != null ? built.Children : [built];
    // One row selected whole is a change of which table the rows come from.
    if (values.Count == 1 && values[0] is RowNode whole) {
      select.ResultSet = whole.RowSet;
      select.Entity = whole.Entity;
      return select;
    }
    // A projection is outermost of its select wherever it was written, so what follows it reads the projected row
    // through a derived table.
    select.Stage = SelectStage.Projecting;
    // By name, a projection over a grouping keeps the keys in front, a key the projection names itself giving way to
    // that slot; from C#, the selector names the key itself where it wants it.
    var slotNames = new string[values.Count];
    for (var i = 0; i < values.Count; i++)
      // One expression selected needs no name: it comes back as a column of values, called Value where a derived
      // table has to name it.
      slotNames[i] = names?[i] ?? (slots[i] as MemberAccessNode)?.MemberName ?? slots[i].Name
        ?? (values.Count == 1 ? "Value"
          : throw new NotSupportedException($"Slot {i} of the projection has no name to take — give it one with .As(…)."));
    if (selector.Source == null && select.IsGrouped)
      select.SelectList.Children.RemoveAll(kept => slotNames.Contains(kept.Name, StringComparer.OrdinalIgnoreCase));
    else
      select.SelectList.Children.Clear();
    for (var i = 0; i < values.Count; i++)
      select.SelectList.AddChild(new SelectedValueNode(values[i], slotNames[i]));
    select.Entity = shape
      ?? TranslationHelper.BuildRowShape(_model, (select.ResultSet.Entity?.Name ?? "Row") + "Row", select.SelectList.Children);
    return select;
  }

  /// <summary>Builds an aggregate over the whole query, whose root is then one value.</summary>
  /// <remarks>A page, a projection or a grouping under the aggregate is cut off into a derived table, since the
  /// aggregate is over what that select would have returned. An ordering under it is refused: it changes nothing,
  /// and two servers refuse it in a derived table without a limit.</remarks>
  private QueryNode BuildAggregate(MethodCallNode call) => BuildAggregate(call, BuildRowSet(call.Argument(0)));

  /// <summary>Builds an aggregate over a rowset already built, such as the sub-select a list or a local query
  /// became.</summary>
  private QueryNode BuildAggregate(MethodCallNode call, RowSetNode source) {
    var predicate = call.ArgumentCount > 1 && call.MethodName is nameof(Queryable.Count) or nameof(Queryable.LongCount)
      or nameof(Queryable.Any) or nameof(Queryable.All);
    if (predicate)
      source = BuildQueryableCallAs(call, nameof(Queryable.Where), source);
    if (source is SelectNode { Stage: > SelectStage.Aggregating } ordered && !ordered.OrderBy.IsEmpty && ordered.Skip.IsEmpty && ordered.Take.IsEmpty)
      throw new NotSupportedException(
        $"{call.MethodName} over an ordered set — an ordering does not change an aggregate, so drop it.");
    // A page, a projection or a grouping under the aggregate is cut off: the aggregate is over that select's rows.
    if (source is SelectNode { } cut && (!cut.Take.IsEmpty || !cut.Skip.IsEmpty || cut.Stage <= SelectStage.Projecting || cut.IsGrouped)) {
      // A page of a list is tied to the outer row, which a derived table cannot see without LATERAL.
      if (cut.OwnerSelect != null && (!cut.Take.IsEmpty || !cut.Skip.IsEmpty))
        throw new NotSupportedException(
          $"{call.MethodName} over a page of a list — the page would be a derived table tied to the row. Aggregate the "
          + "list itself, or spread the page with SelectMany and aggregate the result.");
      source = new DerivedRowSetNode(cut) { Alias = MakeAlias() };
    }
    var select = GetSelectAtStage(source, SelectStage.Aggregating);
    var row = GetLambdaRow(select);
    // The select is the value where it stands as a scalar sub-query, so it carries the aggregate's type.
    select.DataType = call.DataType;
    switch (call.MethodName) {
      case nameof(Queryable.Any):
        return new ExistsNode(select);
      case nameof(Queryable.All):
        // All(x => …) is NOT EXISTS (… WHERE NOT …); the predicate went into the WHERE above and is negated here.
        select.Where.Children[^1] = TranslationHelper.MakeNot(select.Where.Children[^1]);
        return TranslationHelper.MakeNot(new ExistsNode(select));
      case nameof(Queryable.Count):
      case nameof(Queryable.LongCount):
        select.SelectList.Children.Clear();
        select.SelectList.AddChild(new AggregateValueNode(AggregateType.Count, null, call.DataType));
        return select;
      default: {
        if (call.ArgumentCount < 2)
          throw new NotSupportedException($"{call.MethodName} over a sequence of values needs a selector — {call.MethodName}(x => …).");
        var opType = call.MethodName switch {
          nameof(Queryable.Sum) => AggregateType.Sum,
          nameof(Queryable.Min) => AggregateType.Min,
          nameof(Queryable.Max) => AggregateType.Max,
          _ => AggregateType.Average,
        };
        select.SelectList.Children.Clear();
        select.SelectList.AddChild(new AggregateValueNode(opType, BuildValue(call.GetLambda(1), row), call.DataType));
        return select;
      }
    }
  }
}
