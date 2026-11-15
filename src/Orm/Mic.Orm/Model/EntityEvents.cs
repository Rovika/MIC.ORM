using Mic.Orm.Runtime;

namespace Mic.Orm.Model;

/// <summary>Represents the method that handles an event of <see cref="EntityEvents"/>.</summary>
/// <param name="record">The record the event is about; <c>record.EntityInstance</c> is the typed view.</param>
public delegate void EntityEventHandler(EntityRecord record);

/// <summary>Provides the events of one entity type, raised per record and held by
/// <see cref="EntityTypeInfo.Events"/>.</summary>
/// <remarks>Handlers are hooked at startup, from the declaring module's <see cref="EntityModule.OnModelConstructed"/>,
/// and never removed. The events are synchronous and raised inside the save; a handler with real I/O to do belongs on
/// a session-level event of <see cref="EntityAppEvents"/>.</remarks>
public class EntityEvents {
  /// <summary>Occurs when a new entity has been created and belongs to the session.</summary>
  /// <remarks>Raised after the record is new and its <c>[Auto]</c> key is minted, so a handler can write to any
  /// member, including the key, and can read the key the entity will be saved under.</remarks>
  public event EntityEventHandler? New;

  internal void OnNew(EntityRecord record) => New?.Invoke(record);

  /// <summary>Occurs when a row has arrived from the database and its values are on the record.</summary>
  /// <remarks>Raised only where values landed. A row for an entity the session already holds is discarded in favor of
  /// the tracked instance, and nothing is raised for it.</remarks>
  public event EntityEventHandler? Loaded;

  /// <summary>Occurs when a record has become something the next save has to write.</summary>
  /// <remarks>Raised on the transition of a loaded or stub record to modified, not on every write. A new or deleted
  /// record is already in the save set and does not raise it.</remarks>
  public event EntityEventHandler? Modified;

  /// <summary>Occurs when a record is about to be marked for deletion.</summary>
  /// <remarks>Raised before the status changes, so a handler can read the record, follow its references, or throw to
  /// stop the deletion. Not raised for a new entity, which is simply forgotten, or for one already marked.</remarks>
  public event EntityEventHandler? Deleting;

  /// <summary>Occurs when a record is being checked before a save, where an application's own rules go.</summary>
  /// <remarks>Raised before the model's own checks, so a value written here is seen by them. A handler reports a fault
  /// with <see cref="EntityRecord.AddValidationFault"/>; every fault from every record is thrown together. Not raised
  /// for a record being deleted.</remarks>
  public event EntityEventHandler? ValidatingChanges;

  /// <summary>Occurs when a record has been found faulty, as its <see cref="EntityRecord.ValidationFaults"/> say.
  /// </summary>
  /// <remarks>Raised per record, before the exception is thrown. The save is refused regardless.</remarks>
  public event EntityEventHandler? ValidationFailed;

  internal void OnValidatingChanges(EntityRecord record) => ValidatingChanges?.Invoke(record);
  internal void OnValidationFailed(EntityRecord record) => ValidationFailed?.Invoke(record);

  internal void OnLoaded(EntityRecord record) => Loaded?.Invoke(record);
  internal void OnModified(EntityRecord record) => Modified?.Invoke(record);
  internal void OnDeleting(EntityRecord record) => Deleting?.Invoke(record);

  // ----- the save ---------------------------------------------------------------------------------------------------
  //
  // Every record is announced as saving, then the statements run, then every record is saved. A save that threw
  // announces aborted instead of saved, never both.

  /// <summary>Occurs when a record is about to be written, the last moment a handler may change the row.</summary>
  /// <remarks>Raised before the statements are planned and before the auto values are stamped, so a write here lands
  /// in the same statement and still gets its updated-on stamp, and an entity a handler creates here joins this save
  /// with its stamps and its key.</remarks>
  public event EntityEventHandler? SavingChanges;

  /// <summary>Occurs when the save has succeeded; <see cref="EntityRecord.StatusBeforeSave"/> says whether the row was
  /// an insert, an update or a delete.</summary>
  /// <remarks>Every saved record is loaded by now, or a fantom if it was deleted, and a key the server generated is
  /// on its record. Raised after the session's pending set has been cleared, so an entity a handler creates or
  /// changes here is pending for the next save.</remarks>
  public event EntityEventHandler? SavedChanges;

  /// <summary>Occurs when the save has thrown: nothing was committed and the records are still pending.</summary>
  /// <remarks>For undoing what a handler did in memory. A caller that fixes the cause can save again.</remarks>
  public event EntityEventHandler? SaveChangesAborted;

  // Guards work the caller would otherwise do for nobody: a snapshot of the save set.
  internal bool HasSavingChanges => SavingChanges != null;

  internal void OnSavingChanges(EntityRecord record) => SavingChanges?.Invoke(record);
  internal void OnSavedChanges(EntityRecord record) => SavedChanges?.Invoke(record);
  internal void OnSaveChangesAborted(EntityRecord record) => SaveChangesAborted?.Invoke(record);

  // ----- the rule for every event added here --------------------------------------------------------------------
  //
  // Nothing is built for an event nobody subscribed to: the call site passes ingredients, never a built object;
  // anything built here goes inside the `?.`, which skips argument evaluation when the event is null; and where the
  // ingredients themselves cost something, a `HasXxx` lets the call site test first.
}
