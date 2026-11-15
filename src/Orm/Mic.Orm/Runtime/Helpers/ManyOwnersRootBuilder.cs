using Mic.Linq;
using Mic.Linq.Nodes;
using Mic.Linq.Translation;
using Mic.Orm.Model;

namespace Mic.Orm.Runtime;

/// <summary>Builds the many-owners form of a caller's query over a queryable property: the same chain, asked of every
/// sibling in one statement, from the one-owner tree the converter made.</summary>
/// <remarks><para>The chain is filters, then an ordering, then a page, returning whole rows, one row, one value or a
/// projection; a <c>Distinct</c>, a join or anything else has no many-owners form. <c>First</c>, <c>Single</c>,
/// <c>Last</c> and their <c>OrDefault</c> forms are a page of one, or two for <c>Single</c>, so that "more than one"
/// is still told.</para>
/// <para>The owners are one parameter appended to the arguments the scan found, bound as the rows of a parameter; a
/// row is among the owners' by an EXISTS over those rows on every key column. The owner's key rides beside each row
/// as one slot per key column, <see cref="QueryablePropertyHelper.OwnerKeySlot"/> plus the position, with the row or
/// value in <see cref="QueryablePropertyHelper.RowSlot"/>. One instance builds one form; the state the shapes share
/// is its fields.</para></remarks>
internal sealed class ManyOwnersRootBuilder {
  readonly EntityMemberInfo _member;
  readonly ListInfo _info;
  // The reference back from the target, or from the link, to the owner.
  readonly EntityMemberInfo _inverse;
  // For a many-to-many, the link's reference onward to the target; null for a one-to-many.
  readonly EntityMemberInfo? _onward;
  // The property's own root at the bottom of the caller's chain.
  readonly QueryNode _bottom;
  // The argument object the scan carries: the siblings' records.
  readonly object _owners;
  readonly IReadOnlyList<EntityMemberInfo> _ownerMembers;
  // The caller's calls, outermost first, down to the one that sits on the property's root.
  readonly List<MethodCallNode> _calls = [];

  /// <summary>Initializes a new instance of the <see cref="ManyOwnersRootBuilder"/> class.</summary>
  /// <param name="rowSet">The property's set at the bottom of the caller's tree.</param>
  /// <param name="owners">The argument object the scan carries: the siblings' records.</param>
  public ManyOwnersRootBuilder(IMemberRowSet rowSet, object owners) {
    ArgumentNullException.ThrowIfNull(rowSet);
    ArgumentNullException.ThrowIfNull(owners);
    _member = rowSet.Member;
    _info = _member.ListInfo!;
    _inverse = _info.InverseMember!;
    _onward = _info.OtherMember;
    _bottom = rowSet.Node ?? throw new InvalidOperationException("The property's root was not built into this tree.");
    _owners = owners;
    _ownerMembers = _member.GetOwnerMembers();
  }

  /// <summary>Builds the many-owners tree.</summary>
  /// <param name="root">The caller's query, converted.</param>
  /// <returns>The tree, or <see langword="null"/> for a chain the form cannot be written for, which then runs for its
  /// one owner.</returns>
  public QueryNode? Build(QueryNode root) {
    ArgumentNullException.ThrowIfNull(root);
    if (!TryCollectCalls(root))
      return null;
    if (_calls.Count > 0 && IsAggregate(_calls[0].MethodName))
      return BuildAggregate();
    if (_calls.Count > 0 && _calls[0] is { MethodName: nameof(Queryable.Select), ChildCount: 2 } select
        && select.Children[1] is LambdaNode { ParameterCount: 1 })
      return BuildProjection(select);
    if (_calls.Count > 0 && _calls[0].IsFirstLastSingle() && !RewriteOneRowAsPage())
      return null;
    if (!IsBatchableChain(_calls, out var paged))
      return null;
    return paged ? BuildPaged() : BuildUnpaged();
  }

  /// <summary>Collects the caller's calls, outermost first, down to the property's root.</summary>
  /// <returns><see langword="false"/> if something on the way is not a call, which the form cannot carry.</returns>
  private bool TryCollectCalls(QueryNode root) {
    for (var node = root; !ReferenceEquals(node, _bottom); node = node.Children[0]) {
      if (node is not MethodCallNode { ChildCount: > 0 } call)
        return false;
      _calls.Add(call);
    }
    return true;
  }

  /// <summary>Builds an aggregate as a value per owner over the unpaged rows grouped by the owner's key, typed as the
  /// caller's call was, so that <c>Average</c> over an int is still a double; an owner with no rows has no group.
  /// </summary>
  /// <remarks>A filter below the aggregate narrows the rows, an ordering is nothing to it, and a page under one is
  /// not this shape.</remarks>
  private QueryNode? BuildAggregate() {
    var aggregate = _calls[0];
    _calls.RemoveAt(0);
    if (_calls.Any(c => c.MethodName != nameof(Queryable.Where) && !IsOrdering(c.MethodName)))
      return null;
    var rows = BuildRows(ordered: false);
    var counted = aggregate.MethodName is nameof(Queryable.Count) or nameof(Queryable.LongCount) or nameof(Queryable.Any);
    if (counted && aggregate.ChildCount > 1)
      rows = rows.Where(GetLambdaBody(aggregate, ReachTarget));
    var valueType = aggregate.DataType ?? aggregate.ReturnType;
    return rows.GroupBy(GetOwnerKeySlots)
      .Select(group => {
        var selector = counted ? null : GetLambdaBody(aggregate, ReachTarget);
        var value = aggregate.MethodName switch {
          nameof(Queryable.Sum) => group.Sum(selector!),
          nameof(Queryable.Min) => group.Min(selector!),
          nameof(Queryable.Max) => group.Max(selector!),
          nameof(Queryable.Average) => group.Average(selector!),
          _ => group.Count(),
        };
        if (!counted)
          value.DataType = valueType;
        return [value.As(QueryablePropertyHelper.RowSlot)];
      });
  }

  /// <summary>Builds a projection as the caller's slots, under the caller's names, with the owner's key first; one
  /// value is one slot, and a whole row among the slots is a record.</summary>
  private QueryNode? BuildProjection(MethodCallNode select) {
    _calls.RemoveAt(0);
    if (!IsBatchableChain(_calls, out var pagedBelow) || pagedBelow)
      return null;
    return BuildRows(ordered: true).Select(row => {
      var slots = new List<QueryNode>(GetOwnerKeySlots(row));
      var body = GetLambdaBody(select, ReachTarget)(row);
      if (body is NewNode composite)
        for (var i = 0; i < composite.ChildCount; i++) {
          if (composite.SlotNames?[i] is { } name)
            composite.Children[i].Name = name;
          slots.Add(composite.Children[i]);
        }
      else
        slots.Add(body.As(QueryablePropertyHelper.RowSlot));
      return slots.ToArray();
    });
  }

  /// <summary>Rewrites <c>First</c>, <c>Single</c>, <c>Last</c> or an <c>OrDefault</c> form at the top of the chain
  /// into the page of one the translator would make of it: the predicate a filter, <c>Last</c> the ordering reversed,
  /// <c>Take(1)</c>, or 2 for <c>Single</c>; the calls then read as a paged chain.</summary>
  /// <returns><see langword="false"/> if the chain under the call is not filters and orderings.</returns>
  /// <remarks>Rebuilt by name over the property's root, bottom up: a call's name is not writable, and <c>Last</c> has
  /// to say <c>OrderByDescending</c> where the caller said <c>OrderBy</c>. Unordered, a one-row query is ordered by
  /// the key, as the translator does, or the page inside the spread would be refused for lacking an ordering.
  /// </remarks>
  private bool RewriteOneRowAsPage() {
    var oneRow = _calls[0];
    _calls.RemoveAt(0);
    if (!IsBatchableChain(_calls, out var pagedBelow) || pagedBelow)
      return false;
    var last = oneRow.MethodName.StartsWith("Last", StringComparison.Ordinal);
    var source = _bottom;
    for (var i = _calls.Count - 1; i >= 0; i--) {
      var call = _calls[i];
      source = call.MethodName == nameof(Queryable.Where)
        ? source.Where(GetLambdaBody(call, row => row))
        : source.OrderBy(GetLambdaBody(call, row => row), call.MethodName.EndsWith("Descending", StringComparison.Ordinal) != last);
    }
    if (oneRow.ChildCount > 1)
      source = source.Where(GetLambdaBody(oneRow, row => row));
    if (!_calls.Any(c => IsOrdering(c.MethodName)) && _info.TargetEntity.PrimaryKey is { } key)
      foreach (var part in key.MembersExpanded)
        source = source.OrderBy(row => row.Member(part.Member), last);
    var take = source.Take(QueryMaker.Constant(oneRow.MethodName.StartsWith("Single", StringComparison.Ordinal) ? 2 : 1));
    _calls.Clear();
    for (var node = take; !ReferenceEquals(node, _bottom); node = node.Children[0])
      _calls.Add((MethodCallNode)node);
    return true;
  }

  /// <summary>Builds a paged chain as a <c>SelectMany</c> over the owners, the chain as the converter left it given
  /// each owner's rows as its root.</summary>
  /// <remarks>The nodes are mutable, so re-rooting the chain is assigning a child. The owners are said once, of the
  /// owners' own rows; the translator says them again inside the numbered rows of the page, a one-to-many's and a
  /// link's alike.</remarks>
  private QueryNode BuildPaged() {
    var ownerKey = _inverse.ReferenceInfo!.ToKey.GetMembers();
    return QueryMaker.From(_member.Entity!)
      .Where(owner => MemberQueryHelper.BuildListAnyQuery(owner, ownerKey, MakeOwnersParameter(), ownerKey))
      .SelectMany(
        owner => {
          var rows = owner.Member(_member);
          if (_calls.Count == 0)
            return rows;
          _calls[^1].Children[0] = rows;
          return _calls[0];
        },
        (owner, row) => [.. GetKeySlots(owner, ownerKey), row.As(QueryablePropertyHelper.RowSlot)]);
  }

  /// <summary>Builds an unpaged chain as the rows of every owner at once: a one-to-many's rows say whose they are by
  /// their own foreign key; through a link, the link says, with the target read through it.</summary>
  private QueryNode BuildUnpaged() =>
    _onward == null
      ? BuildRows(ordered: true)
      : BuildRows(ordered: true).Select(link => [.. GetOwnerKeySlots(link), link.Member(_onward).As(QueryablePropertyHelper.RowSlot)]);

  /// <summary>Builds the unpaged form's rows: the target's, or the link's, of every owner at once, with the chain's
  /// filters and, where the form keeps an order, its orderings said again over them.</summary>
  private QueryNode BuildRows(bool ordered) {
    var rows = _onward == null
      ? QueryMaker.From(_info.TargetEntity).Where(row => MemberQueryHelper.BuildListAnyOwnerQuery(row, _inverse, MakeOwnersParameter()))
      : QueryMaker.From(_info.LinkEntity!).Where(link => MemberQueryHelper.BuildListAnyOwnerQuery(link, _inverse, MakeOwnersParameter()));
    for (var i = _calls.Count - 1; i >= 0; i--) {
      var call = _calls[i];
      if (call.MethodName == nameof(Queryable.Where))
        rows = rows.Where(GetLambdaBody(call, ReachTarget));
      else if (ordered && IsOrdering(call.MethodName))
        rows = rows.OrderBy(GetLambdaBody(call, ReachTarget), call.MethodName.EndsWith("Descending", StringComparison.Ordinal));
    }
    return rows;
  }

  /// <summary>Makes the parameter the owners are bound as, read off the owners object the scan carries.</summary>
  private QueryParameterNode MakeOwnersParameter() {
    var owners = QueryMaker.Parameter("owners", typeof(EntityRecord[]));
    owners.SetArgValueGetter(_owners, static ownersObject => ownersObject);
    return owners;
  }

  /// <summary>Reaches the target's row the caller wrote over from the row the form iterates: the row itself for a
  /// one-to-many, the far side of the link otherwise.</summary>
  private QueryNode ReachTarget(QueryNode row) => _onward == null ? row : row.Member(_onward);

  /// <summary>Gets the owner's key beside a row, one slot per key column, read off the row's foreign key.</summary>
  private QueryNode[] GetOwnerKeySlots(QueryNode row) => GetKeySlots(row, _ownerMembers);

  /// <summary>Reads the members of a key off a row, each as the owner-key slot of its position, whichever side of
  /// the reference the row is on.</summary>
  private static QueryNode[] GetKeySlots(QueryNode row, IReadOnlyList<EntityMemberInfo> members) {
    var slots = new QueryNode[members.Count];
    for (var i = 0; i < slots.Length; i++)
      slots[i] = row.Member(members[i]).As(QueryablePropertyHelper.OwnerKeySlot + i);
    return slots;
  }

  /// <summary>Gets the body of a call's lambda written over the target's row, which <paramref name="target"/>
  /// reaches from the row the new form iterates.</summary>
  private static Func<QueryNode, QueryNode> GetLambdaBody(MethodCallNode call, Func<QueryNode, QueryNode> target) {
    var lambda = (LambdaNode)call.Children[1];
    return row => lambda.Body.Transform(node => ReferenceEquals(node, lambda.Children[0]) ? target(row) : node);
  }

  /// <summary>Determines whether the calls, outermost first, are filters, then orderings, then a page of
  /// <c>Skip</c> and <c>Take</c> in that order, and whether a page is present.</summary>
  private static bool IsBatchableChain(List<MethodCallNode> calls, out bool paged) {
    var at = calls.Count - 1;
    while (at >= 0 && calls[at].MethodName == nameof(Queryable.Where))
      at--;
    while (at >= 0 && IsOrdering(calls[at].MethodName))
      at--;
    paged = false;
    if (at >= 0 && calls[at].MethodName == nameof(Queryable.Skip)) {
      at--;
      paged = true;
    }
    if (at >= 0 && calls[at].MethodName == nameof(Queryable.Take)) {
      at--;
      paged = true;
    }
    return at < 0;
  }

  private static bool IsOrdering(string method) => method is nameof(Queryable.OrderBy) or nameof(Queryable.OrderByDescending)
    or nameof(Queryable.ThenBy) or nameof(Queryable.ThenByDescending);

  private static bool IsAggregate(string method) => method is nameof(Queryable.Count) or nameof(Queryable.LongCount)
    or nameof(Queryable.Any) or nameof(Queryable.Sum) or nameof(Queryable.Min) or nameof(Queryable.Max)
    or nameof(Queryable.Average);
}
