using Mic.Orm.Model;

namespace Mic.Orm.Runtime;

/// <summary>Holds what a session remembers about its records: the identity map, the save set and the changed lists.
///</summary>
/// <remarks>The identity map holds weak references, so that reading large tables does not exhaust memory; the save
/// set holds strong references in the order the records became pending, which keeps a dirty record alive until it is
/// saved. The tracker is not thread-safe.</remarks>
public sealed class EntityTracker {
  /// <summary>Gets the number of identities in the map, live or not.</summary>
  public int LoadedCount => _loaded.Count;

  /// <summary>Gets the records with a statement pending: the new, the changed and the deleted, in the order they
  /// became so.</summary>
  public IReadOnlyList<EntityRecord> Modified => _modified;

  /// <summary>Gets the lists the application has changed since they loaded or were last saved.</summary>
  public IReadOnlyList<IMemberEntityList> ChangedLists => _changedLists;

  /// <summary>Gets a value that indicates whether a save has anything to look at.</summary>
  public bool HasChanges => _modified.Count > 0 || _changedLists.Count > 0;

  readonly Dictionary<EntityKey, WeakReference<EntityRecord>> _loaded = [];
  readonly List<EntityRecord> _modified = [];
  readonly List<IMemberEntityList> _changedLists = [];
  Dictionary<(EntityRecord Record, EntityMemberInfo Member), object?>? _originals;

  // ----- when the identity map is swept ------------------------------------------------------------------------------
  //
  // A weak map does not shrink by itself: a collected record leaves its key and its WeakReference, a GC handle, in the
  // map. A sweep runs when the map has grown by a fraction of what was live at the last sweep and a collection has
  // happened since, as a walk without one cannot find anything. FindTrackedRecords drops dead entries as it walks.
  const int SweepFloor = 256;                 // below this, a map is not worth walking
  int _sweepAt = SweepFloor;
  int _gcAtLastSweep = -1;                    // -1: no sweep yet, so the first one is never blocked by the gate

  // ----- the identity map --------------------------------------------------------------------------------------------

  /// <summary>Gets the record tracked under an identity.</summary>
  /// <param name="key">The identity to look up.</param>
  /// <returns>The tracked record, or <see langword="null"/> if none is tracked or it has been collected.</returns>
  /// <remarks>An entry whose record has been collected is dropped on the way past.</remarks>
  public EntityRecord? TryGetTracked(EntityKey key) {
    if (!_loaded.TryGetValue(key, out var reference))
      return null;
    if (reference.TryGetTarget(out var record))
      return record;
    _loaded.Remove(key);
    return null;
  }

  /// <summary>Puts a record in the identity map, and sweeps the map when it has grown enough since the last sweep and
  /// a collection has happened.</summary>
  /// <param name="key">The record's identity.</param>
  /// <param name="record">The record to track.</param>
  public void Track(EntityKey key, EntityRecord record) {
    _loaded[key] = new WeakReference<EntityRecord>(record);
    if (_loaded.Count >= _sweepAt && GC.CollectionCount(0) != _gcAtLastSweep)
      RemoveDeadReferences();
  }

  /// <summary>Drops an identity from the map, for a deleted row or one saved to be forgotten.</summary>
  /// <param name="key">The identity to drop.</param>
  public void Forget(EntityKey key) => _loaded.Remove(key);

  /// <summary>Gets the tracked records of one entity that pass a filter.</summary>
  /// <param name="entity">The entity whose records to find.</param>
  /// <param name="filter">The condition a record has to pass.</param>
  /// <returns>The matching records.</returns>
  /// <remarks>The walk visits every entry of the map and drops the entries whose records have been
  /// collected.</remarks>
  public List<EntityRecord> FindTrackedRecords(EntityTypeInfo entity, Func<EntityRecord, bool> filter) {
    var records = new List<EntityRecord>();
    List<EntityKey>? dead = null;
    foreach (var (key, reference) in _loaded) {
      if (!reference.TryGetTarget(out var record)) {
        (dead ??= []).Add(key);
        continue;
      }
      if (ReferenceEquals(key.Entity, entity) && filter(record))
        records.Add(record);
    }
    if (dead != null)
      foreach (var key in dead)
        _loaded.Remove(key);
    return records;
  }

  /// <summary>Drops the entries whose records have been collected.</summary>
  /// <returns>The number of entries dropped.</returns>
  /// <remarks><see cref="Track"/> calls this when the map has grown enough since the last sweep and a collection has
  /// happened; a caller that has just finished with a great many records may call it directly. The next threshold is
  /// set from what survived, so the overshoot allowed stays proportional to the live set. The two numbers are round
  /// rather than measured.</remarks>
  public int RemoveDeadReferences() {
    var dead = new List<EntityKey>();
    foreach (var (key, reference) in _loaded)
      if (!reference.TryGetTarget(out _))
        dead.Add(key);
    foreach (var key in dead)
      _loaded.Remove(key);

    _sweepAt = _loaded.Count + Math.Max(SweepFloor, _loaded.Count / 8);
    _gcAtLastSweep = GC.CollectionCount(0);
    return dead.Count;
  }

  // ----- the save set, and the changed lists -------------------------------------------------------------------------

  /// <summary>Adds a record to the save set.</summary>
  /// <param name="record">The record that has become something the next save acts on.</param>
  /// <remarks>Called on the transition rather than on every write.</remarks>
  public void AddModified(EntityRecord record) => _modified.Add(record);

  /// <summary>Removes a record from the save set, as for a new entity deleted before it was saved.</summary>
  /// <param name="record">The record to remove.</param>
  public void RemoveModified(EntityRecord record) => _modified.Remove(record);

  public void ClearModified() {
    _modified.Clear();
    _originals?.Clear();
  }

  // ----- the values changed unique-key columns had in their rows -----------------------------------------------------

  /// <summary>Gets a value that indicates whether the row's value is kept for any changed column.</summary>
  public bool HasOriginals => _originals is { Count: > 0 };

  /// <summary>Keeps the value a column had in its row; a later call for the same column changes nothing.</summary>
  /// <param name="record">The record of the row.</param>
  /// <param name="member">The column.</param>
  /// <param name="value">The value the column held before its first change.</param>
  public void KeepOriginal(EntityRecord record, EntityMemberInfo member, object? value) =>
    (_originals ??= []).TryAdd((record, member), value);

  /// <summary>Gets the value a column had in its row before the session changed it.</summary>
  /// <param name="record">The record of the row.</param>
  /// <param name="member">The column.</param>
  /// <param name="value">When this method returns, the value kept, if one is.</param>
  /// <returns><see langword="true"/> if a value is kept for the column; otherwise, <see langword="false"/>.</returns>
  public bool TryGetOriginal(EntityRecord record, EntityMemberInfo member, out object? value) {
    if (_originals != null)
      return _originals.TryGetValue((record, member), out value);
    value = null;
    return false;
  }

  /// <summary>Adds a list to the changed lists.</summary>
  /// <param name="list">The list that has become something the next save looks at.</param>
  public void AddChangedList(IMemberEntityList list) => _changedLists.Add(list);

  public void ClearChangedLists() => _changedLists.Clear();
}
