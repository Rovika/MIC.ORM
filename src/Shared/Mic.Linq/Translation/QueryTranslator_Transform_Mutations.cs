using System.Linq.Expressions;
using Mic.Common;
using Mic.Linq.Model;
using Mic.Linq.Nodes;

namespace Mic.Linq.Translation;

// ----- phase 2: the mutations ------------------------------------------------------------------------------------------

public sealed partial class QueryTranslator {
  /// <summary>Builds a delete: the table whose rows go, and the WHERE saying which, as the query's own conditions
  /// where the query is that table filtered and nothing more; otherwise the rows whose key the query finds, the
  /// finding select a derived table selecting the key, joined to the target on it.</summary>
  /// <remarks>The query is built as a read would be, so an extension's filter, a joined reference or a page say for
  /// the delete what they say for a select. The key is found off the rows themselves where the query is over the
  /// target entity, and off the projection where it is over anything else. The SQL builder spells the join as
  /// <c>key IN (select)</c>, an <c>EXISTS</c> for a composite key, or the join itself on the one server that refuses
  /// a sub-query reading the table it deletes from.</remarks>
  private DeleteNode BuildDelete(MethodCallNode call) {
    var entity = call.Entity
      ?? throw new InvalidOperationException("A delete names no entity — its marker was not bound.");
    var keys = entity.PrimaryKeyMembers;
    var select = EnsureSelect(BuildRowSet(call.Argument(0)));
    var own = select.SelectList.IsEmpty && !select.IsGrouped;
    if (own && !ReferenceEquals(select.ResultSet.Entity, entity))
      throw new NotSupportedException(
        $"The query is over '{select.ResultSet.Entity?.Name}' rows and the delete is of '{entity.Name}' — project "
        + $"the key of the '{entity.Name}' to delete: .Select(x => x.{entity.Name}.Id).");
    if (!own && select.SelectList.IsEmpty)
      throw new NotSupportedException(
        "A delete needs rows of an entity to delete — the query groups them, and there is no table those come from. "
        + "Write the filter over the entity set and delete that, or project the key.");
    // An ordering decides nothing about which rows go, unless a page of the ordered rows is what goes.
    if (!select.OrderBy.IsEmpty && select.Skip.IsEmpty && select.Take.IsEmpty)
      throw new NotSupportedException(
        "An ordering on a delete decides nothing — every row the query finds goes. Take a page of the ordered rows, "
        + "or drop the ordering.");
    // The simple form: the table alone, filtered, its conditions the statement's over the same row.
    if (own && select.ResultSet is TableNode alone && select.From.ChildCount == 1 && select.Skip.IsEmpty
        && select.Take.IsEmpty && !select.IsDistinct) {
      var simple = new DeleteNode(alone);
      alone.OwnerSelect = null;
      foreach (var condition in select.Where.Children)
        simple.Where.AddChild(condition);
      return simple;
    }
    // The rows the query finds, by key: a fresh table stands as the target, and the select says which of its rows.
    if (keys.Count == 0)
      throw new NotSupportedException(
        $"'{entity.Name}' has no primary key to find its rows by, which a delete through other tables needs.");
    var delete = new DeleteNode(BuildTable(entity));
    var target = delete.Target;
    // Over the target's own rows the key is the row's; over anything else it is what the projection brought.
    if (own)
      foreach (var key in keys)
        ProjectMember(select, key);
    else
      MatchKeySlots(select, entity, IsBareSelection(call.Argument(0)));
    // The target's own filter, which the select over another entity does not carry.
    if (!own)
      AddExtensionFilter(delete);
    // The finding select as a derived table selecting the key, joined to the target on it.
    var derived = new DerivedRowSetNode(select) { Alias = MakeAlias() };
    var pairing = new QueryNode[keys.Count];
    for (var i = 0; i < keys.Count; i++)
      pairing[i] = TranslationHelper.MakeBinary(ExpressionType.Equal, BuildColumn(target.Row, keys[i]),
        new ColumnNode(derived.Row, (SelectedValueNode)select.SelectList.Children[i]));
    delete.AddChild(new JoinNode(derived, pairing.Length == 1 ? pairing[0] : TranslationHelper.MakeAnd(pairing), JoinType.Inner));
    return delete;
  }

  /// <summary>Builds an update: the table, the SET list and the WHERE. The projection's slots are the values, each
  /// under the name of the member it writes, and the key slots say which rows.</summary>
  /// <remarks>Over the target's own filtered rows the values are expressions over the row and the WHERE is the
  /// query's, and the key need not be projected. Read out of any other query, or over the target's rows with a join,
  /// a page or a grouping, the statement joins the query's derived table on the key, every key member projected,
  /// and takes each value off it. A key slot is never written.</remarks>
  private UpdateNode BuildUpdate(MethodCallNode call) {
    var entity = call.Entity
      ?? throw new InvalidOperationException("An update names no entity — its marker was not bound.");
    var select = EnsureSelect(BuildRowSet(call.Argument(0)));
    if (!IsProjection(select))
      throw new NotSupportedException(
        "An update takes its values from a projection — .Select(x => new { Member = value, … }), each slot named for "
        + "the member it writes.");
    // An ordering decides nothing about which rows are written, unless a page of the ordered rows is what is.
    if (!select.OrderBy.IsEmpty && select.Skip.IsEmpty && select.Take.IsEmpty)
      throw new NotSupportedException(
        "An ordering on an update decides nothing — every row the query names is written. Take a page of the ordered "
        + "rows, or drop the ordering.");
    var keys = entity.PrimaryKeyMembers;
    var keySlots = new SelectedValueNode?[keys.Count];
    var assignments = new List<(ILinqEntityMember Member, SelectedValueNode Slot)>();
    foreach (var (member, slot) in MatchAssignments(select, entity)) {
      var keyAt = IndexOfMember(keys, member);
      if (keyAt >= 0)
        keySlots[keyAt] = slot;
      else
        assignments.Add((member, slot));
    }
    if (assignments.Count == 0)
      throw new NotSupportedException("The projection brings the key and nothing to write — name the members to set.");

    // The simple form: the target's own rows, filtered, the values over the row and the query's conditions the WHERE.
    if (select.ResultSet is TableNode alone && ReferenceEquals(alone.Entity, entity) && select.From.ChildCount == 1
        && !select.IsGrouped && select.Skip.IsEmpty && select.Take.IsEmpty && !select.IsDistinct) {
      var simple = new UpdateNode(alone);
      alone.OwnerSelect = null;
      foreach (var (member, slot) in assignments)
        simple.Set.AddChild(new SelectedValueNode(slot.Source, member.Name));
      foreach (var condition in select.Where.Children)
        simple.Where.AddChild(condition);
      return simple;
    }

    // The values read out of the query: its derived table joined to the target on the key, every key member brought.
    for (var i = 0; i < keys.Count; i++)
      if (keySlots[i] == null)
        throw new NotSupportedException(
          $"The projection brings no '{keys[i].Name}', which the key of '{entity.Name}' needs to say which rows are "
          + "written — project the key's members under their names.");
    var update = new UpdateNode(BuildTable(entity));
    var target = update.Target;
    var derived = new DerivedRowSetNode(select) { Alias = MakeAlias() };
    var pairing = new QueryNode[keys.Count];
    for (var i = 0; i < keys.Count; i++)
      pairing[i] = TranslationHelper.MakeBinary(ExpressionType.Equal, BuildColumn(target.Row, keys[i]), new ColumnNode(derived.Row, keySlots[i]!));
    // The target's own filter rides in the join, so the statement needs no WHERE on any server's spelling of it.
    var onKey = AddFilterToJoin(target, pairing.Length == 1 ? pairing[0] : TranslationHelper.MakeAnd(pairing));
    update.AddChild(new JoinNode(derived, onKey, JoinType.Inner));
    foreach (var (member, slot) in assignments)
      update.Set.AddChild(new SelectedValueNode(new ColumnNode(derived.Row, slot), member.Name));
    return update;
  }

  /// <summary>Builds an insert: the table, and the query's select as the rows, its list transformed to the members it
  /// writes, in order.</summary>
  /// <remarks>The select is the query's own, page, ordering and grouping included. What the projection does not name
  /// is not written, and nothing here checks what the table requires; that refusal is the server's.</remarks>
  private InsertNode BuildInsert(MethodCallNode call) {
    var entity = call.Entity
      ?? throw new InvalidOperationException("An insert names no entity — its marker was not bound.");
    var select = EnsureSelect(BuildRowSet(call.Argument(0)));
    if (!IsProjection(select))
      throw new NotSupportedException(
        "An insert takes its rows from a projection — .Select(x => new { Member = value, … }), each slot named for "
        + "the member it writes.");
    var members = MatchAssignments(select, entity).Select(pair => pair.Member).ToList();
    return new InsertNode(BuildTable(entity), select, members);
  }

  /// <summary>Determines whether a select ends in a projection: values on its list, or a bundle whose slots are kept
  /// aside.</summary>
  private static bool IsProjection(SelectNode select) => !select.SelectList.IsEmpty || select.Entity is BundleShape;

  /// <summary>Matches a projection's slots to the members they write, each with its slot on the select list; where a
  /// slot names a reference, the row it holds becomes one slot per foreign-key column.</summary>
  /// <remarks>A slot is a member of the target or it is refused; a list cannot be assigned; a reference takes a row
  /// of the query, not a value.</remarks>
  private List<(ILinqEntityMember Member, SelectedValueNode Slot)> MatchAssignments(SelectNode select, ILinqEntity entity) {
    var matched = new List<(ILinqEntityMember, SelectedValueNode)>();
    // A projection holding a row is a bundle, whose slots were kept aside rather than written to the list.
    var slots = select.Entity is BundleShape bundle && _bundles.TryGetValue(bundle, out var values)
      ? values.Select((value, i) => (Name: bundle.Slots[i].Name!, Value: value)).ToList()
      : select.SelectList.Children.Select(item => (Name: item.Name!, Value: ((SelectedValueNode)item).Source)).ToList();
    select.SelectList.Children.Clear();
    foreach (var (name, value) in slots) {
      var member = entity.FindMember(name)
        ?? throw new NotSupportedException($"'{name}' is no member of '{entity.Name}', and a mutation writes members.");
      switch (member.Kind) {
        case LinqMemberKind.Value: {
          var slot = new SelectedValueNode(value, member.Name);
          select.SelectList.AddChild(slot);
          matched.Add((member, slot));
          break;
        }
        case LinqMemberKind.Reference: {
          var info = member.ReferenceInfo!;
          if (value is not RowNode row || !ReferenceEquals(TranslationHelper.GetRowEntity(row.Entity!), info.TargetEntity))
            throw new NotSupportedException(
              $"'{entity.Name}.{member.Name}' is a reference, and is written by a ROW of '{info.TargetEntity.Name}' the "
              + "query selects — not by a value.");
          for (var i = 0; i < info.FromMembers.Count; i++) {
            var key = new SelectedValueNode(BuildColumn(row, info.ToMembers[i]), info.FromMembers[i].Name);
            select.SelectList.AddChild(key);
            matched.Add((info.FromMembers[i], key));
          }
          break;
        }
        default:
          throw new NotSupportedException(
            $"'{entity.Name}.{member.Name}' is a {member.Kind.ToString().ToLowerInvariant()}, and cannot be written by a statement.");
      }
    }
    return matched;
  }

  /// <summary>Adds the target table's extension filter to the delete's WHERE, where it has one.</summary>
  private void AddExtensionFilter(DeleteNode delete) {
    if (GetExtensionFilter(delete.Target) is { } filter)
      delete.Where.AddChild(filter);
  }

  /// <summary>Determines whether a query ends in a bare projection of one value and no <c>new</c>, which is the key by
  /// declaration whatever the value is named.</summary>
  /// <remarks>The prepared tree is asked, since by the time a bare <c>o.Product.Id</c> is a slot it is the foreign-key
  /// shortcut and no name would match the target's key.</remarks>
  private static bool IsBareSelection(QueryNode source) =>
    source is MethodCallNode { MethodName: nameof(Queryable.Select) } select && select.IsQueryableCall()
    && select.GetLambda(1).Body is not NewNode;

  /// <summary>Cuts a projection's list down to the target's key, in key order: the one value a bare projection
  /// selected, for a single-column key; the slots named for the key's members otherwise.</summary>
  private static void MatchKeySlots(SelectNode select, ILinqEntity entity, bool bare) {
    var keys = entity.PrimaryKeyMembers;
    var slots = select.SelectList.Children;
    if (bare) {
      if (keys.Count != 1 || slots.Count != 1)
        throw new NotSupportedException(
          $"The key of '{entity.Name}' has {keys.Count} members, and one value was projected — project them under "
          + "their names: new { … }.");
      slots[0].Name = keys[0].Name;
      return;
    }
    var matched = new List<QueryNode>(keys.Count);
    foreach (var key in keys)
      matched.Add(slots.FirstOrDefault(s => string.Equals(s.Name, key.Name, StringComparison.OrdinalIgnoreCase))
        ?? throw new NotSupportedException(
          $"The projection brings no '{key.Name}', which the key of '{entity.Name}' needs — project the key's members "
          + "under their names, or the one value for a single-column key."));
    select.SelectList.Children.Clear();
    foreach (var slot in matched)
      select.SelectList.AddChild(slot);
  }

  /// <summary>Gets the select an operation at a stage adds its clause to: the source itself where it is a select
  /// that has not passed the stage, otherwise a new select over the source, which becomes a derived table where the
  /// chain has gone backwards on it.</summary>
  private SelectNode GetSelectAtStage(RowSetNode source, SelectStage stage) {
    if (source is SelectNode select && stage <= select.Stage) {
      select.Stage = stage;
      return select;
    }
    if (source is SelectNode passed) {
      // An ordering inside a derived table with nothing to cut decides nothing, and two servers refuse it outright;
      // it is refused rather than dropped.
      if (!passed.OrderBy.IsEmpty && passed.Skip.IsEmpty && passed.Take.IsEmpty)
        throw new NotSupportedException(
          "An ordering that ends up inside a derived table decides nothing. Filter before ordering, order the OUTER "
          + "query instead, or take a page of the ordered one.");
      // A bundle's rows read through a derived table would be whole rows on an inner select list.
      if (_bundled.Contains(passed))
        throw new NotSupportedException(
          "A projection that hands whole rows on cannot end up inside a derived table — filter, order and page before "
          + "it, or select the values you need instead of the rows.");
      source = new DerivedRowSetNode(passed) { Alias = MakeAlias() };
    }
    // A set operation read from is a derived table in all but name, and takes the alias one would.
    if (source is SetOperationNode combined)
      combined.Alias ??= MakeAlias();
    return OpenSelect(source, stage);
  }

  private static void RequireOrdered(SelectNode select, string operation) {
    if (select.OrderBy.IsEmpty)
      throw new NotSupportedException(
        $"{operation} needs an ordering — rows skipped or taken from an unordered set are arbitrary.");
  }
}
