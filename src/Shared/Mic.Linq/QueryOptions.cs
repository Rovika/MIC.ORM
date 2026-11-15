namespace Mic.Linq;

/// <summary>Specifies what a caller asked for beyond the query itself, said where the set is named.</summary>
/// <remarks>The lock options are part of the cache key, since they are in the statement's text; the others are not:
/// they change which artifact is used for a query, not what the query says. They are merged across a query's
/// sources, so a query skips the cache if any of its sets asked to.</remarks>
[Flags]
public enum QueryOptions {
  /// <summary>No option.</summary>
  None = 0,

  /// <summary>The query goes to the database even where the entity is held whole in memory, and so does every
  /// sub-query in it over such an entity.</summary>
  SkipEntityCache = 1,

  /// <summary>The rows the root select finds are held for writing until the transaction ends, as <c>FOR UPDATE</c>
  /// or <c>WITH (UPDLOCK)</c>.</summary>
  /// <remarks>This option implies <see cref="SkipEntityCache"/>. It is part of the statement's text, so a locked and
  /// an unlocked read of one shape are two statements.</remarks>
  LockForUpdate = 1 << 1,

  /// <summary>The rows the root select finds are held for writing, and the rows another transaction holds are skipped
  /// rather than waited for, as <c>FOR UPDATE SKIP LOCKED</c> or <c>WITH (UPDLOCK, READPAST)</c>: the query takes the
  /// next free rows, the shape of a table used as a queue.</summary>
  /// <remarks>This option implies <see cref="LockForUpdate"/>.</remarks>
  SkipLocked = 1 << 3,

  /// <summary>The query is translated each time and leaves no entry in the query cache.</summary>
  /// <remarks>For a query the code knows will not repeat, such as a user's search with its own combination of
  /// conditions: the cache is unbounded, and a stream of one-off shapes would grow it for nothing.</remarks>
  BypassQueryCache = 1 << 2,
}
