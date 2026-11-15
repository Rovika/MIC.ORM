using Mic.Common;
using Mic.Orm.Model;

namespace Mic.Orm.Runtime;

/// <summary>Orders the records of a save so that every statement runs after the rows it references exist and before
/// they are deleted.</summary>
/// <remarks>The order comes from each entity type's <see cref="EntityTypeInfo.TopologicalIndex"/>, assigned when the
/// model is built; only the records of a type in a non-trivial topological group are sorted by a graph over the rows
/// themselves, and a save touching none pays nothing.</remarks>
public static class SaveOrderHelper {
  /// <summary>Specifies the five phases of a save, in the order they run.</summary>
  /// <remarks>An isolated type is one nothing in the model references. Its deletes run first and its inserts last, so
  /// a row deleted and re-inserted with the same key in one save succeeds; a referenced type's rows stay in
  /// dependency order, and a delete-and-reinsert of its primary key fails. A unique value other than the primary
  /// key that one record frees and another takes is moved out of the way before the phases run, by
  /// <see cref="UniqueValueVacateHelper"/>. Inserts run before updates because an update may
  /// set a foreign key to a row the save inserts; updates run before deletes because an update may remove the last
  /// reference to a row the save deletes.</remarks>
  enum SavePhase { IsolatedDelete, Insert, Update, Delete, IsolatedInsert }

  /// <summary>Sorts the records of a save into the order their statements must run.</summary>
  /// <param name="records">The records the save writes.</param>
  /// <returns>The records, in the order their statements must run.</returns>
  /// <exception cref="InvalidOperationException">the records reference each other in a circle, so no order of
  /// statements satisfies the foreign keys between them.</exception>
  public static IReadOnlyList<EntityRecord> Sort(IReadOnlyList<EntityRecord> records) {
    ArgumentNullException.ThrowIfNull(records);
    if (records.Count < 2)
      return records;

    if (records.Any(r => r.Entity.Flags.HasFlag(EntityTypeFlags.InTopologyGroup)))
      AssignSubIndexes(records);
    else
      foreach (var record in records)
        record.SortSubIndex = 0;

    var ordered = new List<EntityRecord>(records.Count);
    ordered.AddRange(GetRecordsInPhase(records, SavePhase.IsolatedDelete));
    ordered.AddRange(GetRecordsInPhase(records, SavePhase.Insert));
    ordered.AddRange(GetRecordsInPhase(records, SavePhase.Update));
    ordered.AddRange(GetRecordsInPhase(records, SavePhase.Delete));
    ordered.AddRange(GetRecordsInPhase(records, SavePhase.IsolatedInsert));
    return ordered;
  }

  /// <summary>Gets the phase a record's statement runs in.</summary>
  private static SavePhase GetPhase(EntityRecord record) => record.Status switch {
    EntityStatus.New => record.Entity.Flags.HasFlag(EntityTypeFlags.Referenced) ? SavePhase.Insert : SavePhase.IsolatedInsert,
    EntityStatus.Deleted => record.Entity.Flags.HasFlag(EntityTypeFlags.Referenced) ? SavePhase.Delete : SavePhase.IsolatedDelete,
    _ => SavePhase.Update,
  };

  /// <summary>Gets the records of one phase, in the order the phase runs them.</summary>
  /// <remarks>The sort is stable, so the records of one entity keep the order they were created in and two identical
  /// saves send their rows in one order. Deletes are sorted in reverse: a row is deleted before what it
  /// references.</remarks>
  private static IEnumerable<EntityRecord> GetRecordsInPhase(IReadOnlyList<EntityRecord> records, SavePhase phase) {
    var inPhase = records.Where(r => GetPhase(r) == phase);
    return phase is SavePhase.Delete or SavePhase.IsolatedDelete
      ? inPhase.OrderByDescending(r => r, SaveOrderComparer.Instance)
      : inPhase.OrderBy(r => r, SaveOrderComparer.Instance);
  }

  /// <summary>Compares two records by topological index, then by sub-index, then by entity name.</summary>
  /// <remarks>Sorting by entity name last keeps the records of one type together, so that the planner can collapse a
  /// run of them into one multi-row statement.</remarks>
  private static int Compare(EntityRecord x, EntityRecord y) {
    var result = x.Entity.TopologicalIndex.CompareTo(y.Entity.TopologicalIndex);
    if (result == 0)
      result = x.SortSubIndex.CompareTo(y.SortSubIndex);
    if (result == 0)
      result = string.CompareOrdinal(x.Entity.Name, y.Entity.Name);
    return result;
  }

  /// <summary>Orders the records of a non-trivial topological group among themselves by the references between
  /// them.</summary>
  /// <remarks>A reference between records of different topological indexes is already ordered by the index and adds
  /// no edge. A cycle among the records is a circular reference between rows, which no order of statements satisfies;
  /// it is reported rather than emitted in an order that fails.</remarks>
  private static void AssignSubIndexes(IReadOnlyList<EntityRecord> records) {
    foreach (var record in records)
      record.SortSubIndex = 0;

    var graph = new Graph<EntityRecord>();
    var present = records.Where(r => r.Entity.Flags.HasFlag(EntityTypeFlags.InTopologyGroup)).ToHashSet();
    if (present.Count < 2)
      return;

    foreach (var record in present)
      foreach (var member in record.Entity.Members) {
        if (member.Kind != MemberKind.Reference)
          continue;
        if (record.GetRawValue(member) is not EntityBase target || !present.Contains(target.Record))
          continue;
        if (target.Record.Entity.TopologicalIndex != record.Entity.TopologicalIndex)
          continue;                   // already ordered by the type index; an edge would say nothing new
        graph.AddNode(record);
        graph.AddNode(target.Record);
        graph.AddEdge(record, target.Record);
      }

    if (graph.NodeCount == 0)
      return;                         // nothing in the group actually points at anything else in it

    var index = 1;
    foreach (var component in graph.GetSortedComponents()) {
      if (component.IsCycle)
        throw new InvalidOperationException(
          "These records reference each other in a circle, so no order of statements satisfies the foreign keys "
          + $"between them: {string.Join(", ", component.Nodes)}. Break the cycle by saving in two steps — insert one "
          + "side, then set the reference and save again.");
      foreach (var node in component.Nodes)
        node.SortSubIndex = index++;
    }
  }

  /// <summary>Provides <see cref="SaveOrderHelper.Compare"/> as the comparer the stable <c>OrderBy</c> overloads
  /// take.</summary>
  sealed class SaveOrderComparer : IComparer<EntityRecord> {
    public static readonly SaveOrderComparer Instance = new();
    public int Compare(EntityRecord? x, EntityRecord? y) => SaveOrderHelper.Compare(x!, y!);
  }
}
