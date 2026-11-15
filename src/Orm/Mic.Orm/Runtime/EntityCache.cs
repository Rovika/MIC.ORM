using System.Collections.Concurrent;
using Mic.Linq;
using Mic.Linq.Model;
using Mic.Linq.Nodes;
using Mic.Orm.Model;
namespace Mic.Orm.Runtime;

/// <summary>Holds the rows of the entities declared as cached, so that a read by key or a query over them alone is
/// answered in memory.</summary>
/// <remarks>A block holds detached snapshots; a read copies the rows it answers with into records of the asking
/// session. A block is filled on the first read, dropped after a write under the same context, and refilled after
/// the entity's expiration where one is declared. Whether a query runs here is decided by its translation; a query
/// the evaluator cannot run over cached rows goes to the server.</remarks>
public sealed class EntityCache {
  /// <summary>Represents one entity's cached rows under one discriminator: the list and the lookup by primary key.
  /// </summary>
  public sealed class Block {
    /// <summary>Initializes a new instance of the <see cref="Block"/> class.</summary>
    public Block(IReadOnlyList<EntityRecord> records, Dictionary<EntityKey, EntityRecord> byKey, DateTime filledAt) {
      Records = records;
      ByKey = byKey;
      FilledAt = filledAt;
    }

    /// <summary>Gets the rows, as detached snapshots.</summary>
    public IReadOnlyList<EntityRecord> Records { get; }

    /// <summary>Gets the rows by primary key.</summary>
    public Dictionary<EntityKey, EntityRecord> ByKey { get; }

    /// <summary>Gets when the block was filled, by the application's clock.</summary>
    public DateTime FilledAt { get; }
  }

  readonly ConcurrentDictionary<(EntityTypeInfo Entity, string Discriminator), Block> _blocks = new();
  long _hits;

  /// <summary>Gets the number of reads the cache has answered.</summary>
  public long Hits => Interlocked.Read(ref _hits);

  /// <summary>Gets the number of blocks held.</summary>
  public int BlockCount => _blocks.Count;

  /// <summary>Returns a value that indicates whether the entity is declared as cached.</summary>
  public static bool IsCached(EntityTypeInfo entity) => ((ILinqEntity)entity).IsCached;

  /// <summary>Asynchronously returns the entity's block for the loader's context, filling it where it is absent or
  /// expired.</summary>
  /// <remarks>A block is keyed by the discriminator the entity's filters make of the context, so one tenant never
  /// sees another's rows. The fill reads through a session of its own, on a connection of its own, so that it sees
  /// committed rows only and attaches nothing to the loader. Two fills racing keep the later one.</remarks>
  public async Task<Block> GetBlockAsync(EntityTypeInfo entity, EntitySession loader) {
    var key = (entity, BuildCacheDiscriminator(entity, loader.Context));
    var now = loader.Context.Now;
    if (_blocks.TryGetValue(key, out var cached)
        && (entity.CacheExpiration is not { } expiration || now - cached.FilledAt < expiration))
      return cached;

    await using var filler = new EntitySession(loader.Context) { RunMode = loader.RunMode };
    var loaded = await filler.Database.RequestExecutor.ExecuteAsync(new DataRequest(filler,
      QueryMaker.From(entity), [], QueryOptions.SkipEntityCache, $"fill {entity.Name}"))
      as IReadOnlyList<EntityRecord> ?? [];
    var byKey = new Dictionary<EntityKey, EntityRecord>(loaded.Count);
    foreach (var snapshot in loaded) {
      // Detached from the filler, so nothing can load through the snapshot and no session shares it.
      snapshot.Detach();
      byKey[snapshot.PrimaryKey] = snapshot;
    }
    var block = new Block(loaded, byKey, now);
    _blocks[key] = block;
    return block;
  }

  /// <summary>Drops the block of the entity under the context, so that the next read under it fills a fresh one.
  /// </summary>
  /// <remarks>Call it after the write's transaction has committed; dropped before, another session's read would
  /// refill the block from rows not yet committed.</remarks>
  public void Invalidate(EntityTypeInfo entity, OperationContext context) {
    ArgumentNullException.ThrowIfNull(entity);
    ArgumentNullException.ThrowIfNull(context);
    _blocks.TryRemove((entity, BuildCacheDiscriminator(entity, context)), out _);
  }

  /// <summary>Builds the string that tells one filtered block of an entity from another, given the operation
  /// context: the discriminators of the filters that vary, each followed by a separator.</summary>
  /// <remarks>A block is filled under one caller's filter, so this is what keeps one tenant's block from serving
  /// another. Only the filters that vary contribute.</remarks>
  private static string BuildCacheDiscriminator(EntityTypeInfo entity, object? context) {
    if (entity.Filters.Count == 0)
      return "";
    var key = new System.Text.StringBuilder();
    foreach (var filter in entity.Filters)
      if (filter.Discriminator is { } discriminator)
        key.Append(discriminator(context)).Append('|');
    return key.ToString();
  }

  /// <summary>Asynchronously fills the blocks of the given entities where they are not filled and returns what reads
  /// their rows.</summary>
  public async Task<Func<ILinqEntity, IReadOnlyList<object>>> GetBlocksAsync(IReadOnlyList<ILinqEntity> entities,
      EntitySession session) {
    var blocks = new Dictionary<ILinqEntity, Block>(ReferenceEqualityComparer.Instance);
    foreach (var entity in entities)
      blocks[entity] = await GetBlockAsync((EntityTypeInfo)entity, session);
    return entity => blocks[entity].Records;
  }

  /// <summary>Asynchronously finds the row for a key and returns it as a record of the asking session;
  /// <see langword="null"/> where there is none.</summary>
  public async Task<EntityRecord?> FindByKeyAsync(EntityKey key, EntitySession session) {
    var block = await GetBlockAsync(key.Entity, session);
    Interlocked.Increment(ref _hits);
    return block.ByKey.TryGetValue(key, out var snapshot) ? Copy(snapshot, session) : null;
  }

  /// <summary>Runs a plan over the cached rows the request carries and returns its answer as the asking session's
  /// records where it is rows.</summary>
  public object? Run(CacheQuery plan, DataRequest request) {
    Interlocked.Increment(ref _hits);
    return Materialize(plan.Evaluator.Evaluate(request), request.Session, plan.ReturnsSingleRow);
  }

  /// <summary>Converts what the evaluator answered into the session's own: a snapshot becomes a tracked record, a
  /// list of snapshots a list of records, a value stays a value.</summary>
  /// <remarks>A query that asked for one row answers a list of at most one, as a statement cut to one row does.
  /// </remarks>
  private static object? Materialize(object? result, EntitySession session, bool returnsSingleRow) {
    switch (result) {
      case null:
        return returnsSingleRow ? new List<EntityRecord>() : null;
      case EntityRecord snapshot: {
        var record = Copy(snapshot, session);
        return returnsSingleRow ? new List<EntityRecord> { record } : record;
      }
      case IEnumerable<object?> rows: {
        var items = rows.ToList();
        if (items.Count > 0 && items.TrueForAll(item => item is EntityRecord))
          return items.ConvertAll(item => Copy((EntityRecord)item!, session));
        return items;
      }
      default:
        return result;
    }
  }

  /// <summary>Copies a snapshot into a tracked record of the session.</summary>
  private static EntityRecord Copy(EntityRecord snapshot, EntitySession session) {
    var record = new EntityRecord(snapshot.Entity, session);
    record.FillFrom(snapshot);
    return session.Attach(record);
  }
}
