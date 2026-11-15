using System.Collections.Concurrent;
using Mic.Linq;
using Mic.Linq.Nodes;
using Mic.Orm.Db.Sql;
using Mic.Orm.Runtime;

namespace Mic.Orm.Db;

/// <summary>Represents what one query shape translates to: the template of the statement that answers it from the
/// server, and the query that answers it from memory where its entities are cached.</summary>
/// <remarks>A shape normally answered from memory still has its template, which a caller setting
/// <see cref="QueryOptions.SkipEntityCache"/> needs. <see cref="CacheQuery"/> being <see langword="null"/> means
/// memory cannot answer the shape.</remarks>
public sealed class QueryCacheEntry {
  SqlTemplate? _serverOnlyTemplate;

  /// <summary>Initializes a new instance of the <see cref="QueryCacheEntry"/> class.</summary>
  /// <param name="cacheQuery">The query that answers the shape from the entity cache, or <see langword="null"/> where
  /// nothing can.</param>
  /// <param name="sqlTemplate">The template of the statement that answers the shape from the server, or
  /// <see langword="null"/> where there is none.</param>
  /// <param name="includes">The includes the shape was written with, or <see langword="null"/> for none.</param>
  public QueryCacheEntry(CacheQuery? cacheQuery, SqlTemplate? sqlTemplate, IReadOnlyList<QueryInclude>? includes = null) {
    CacheQuery = cacheQuery;
    SqlTemplate = sqlTemplate;
    Includes = includes ?? [];
  }

  /// <summary>Gets the template of the statement that answers this shape from the server, or <see langword="null"/>
  /// where there is none.</summary>
  public SqlTemplate? SqlTemplate { get; }

  /// <summary>Gets the query that answers this shape from the entity cache, or <see langword="null"/> where nothing
  /// can.</summary>
  public CacheQuery? CacheQuery { get; }

  /// <summary>Gets the includes the shape was written with, which every call of the shape loads with the rows.
  /// </summary>
  /// <remarks>The includes are part of the shape's key. Most shapes have none.</remarks>
  public IReadOnlyList<QueryInclude> Includes { get; }

  /// <summary>Gets the template of the statement that reads every entity of the shape from the server, making it
  /// on the first call.</summary>
  /// <param name="build">A function that translates the shape with no sub-query answered from memory.</param>
  /// <returns>The template.</returns>
  /// <remarks>Only a shape whose <see cref="SqlTemplate"/> takes a sub-query over a cached entity as a parameter
  /// has a second statement; for any other the two are one. Two callers racing make it twice and keep one.
  /// </remarks>
  internal SqlTemplate GetServerOnlyTemplate(Func<SqlTemplate> build) {
    if (_serverOnlyTemplate is { } made)
      return made;
    var built = build();
    return Interlocked.CompareExchange(ref _serverOnlyTemplate, built, null) ?? built;
  }

  /// <summary>Gets the entry of the many-owners form of a shape rooted at a queryable property, which the smart load
  /// runs for every owner at once.</summary>
  /// <value><see langword="null"/> until the smart load first asks; an entry with no <see cref="SqlTemplate"/> where
  /// the many-owners form cannot be written for the chain; the form's entry otherwise.</value>
  /// <remarks>Held here and not in the cache's dictionary: the form is reached through the shape it derives from and
  /// never looked up by a key of its own.</remarks>
  public QueryCacheEntry? ManyOwnersEntry { get; internal set; }
}

/// <summary>Holds the translated queries of one database, one entry per query shape, found by the shape's hash.
/// </summary>
/// <remarks>The key is a 64-bit hash of the shape and the cache is unbounded; the key space is bounded by the number
/// of query shapes an application has because a value that varies with data must be a parameter, never part of the
/// shape.</remarks>
public sealed class QueryCache {
  /// <summary>Gets the number of query shapes held.</summary>
  public int Count => _entries.Count;

  /// <summary>Gets the number of lookups that found their entry.</summary>
  public long Hits => Interlocked.Read(ref _hits);

  /// <summary>Gets the number of translations this cache asked for.</summary>
  /// <remarks>A shape answered from memory has an entry and no statement, and is not counted.</remarks>
  public long Misses => Interlocked.Read(ref _misses);

  readonly ConcurrentDictionary<ulong, QueryCacheEntry> _entries = new();
  long _hits;
  long _misses;

  /// <summary>Finds the entry for a shape without counting a hit.</summary>
  /// <param name="key">The hash of the shape.</param>
  /// <returns>The entry, or <see langword="null"/> if the shape has never been translated.</returns>
  public QueryCacheEntry? Find(ulong key) => _entries.TryGetValue(key, out var entry) ? entry : null;

  /// <summary>Finds the entry for a shape a caller is about to use, counting a hit when it is found.</summary>
  /// <param name="key">The hash of the shape.</param>
  /// <returns>The entry, or <see langword="null"/> if the shape has never been translated.</returns>
  public QueryCacheEntry? Hit(ulong key) {
    if (!_entries.TryGetValue(key, out var entry))
      return null;
    Interlocked.Increment(ref _hits);
    return entry;
  }

  /// <summary>Records what one translation decided about a shape.</summary>
  /// <param name="key">The hash of the shape.</param>
  /// <param name="entry">The entry the translation made.</param>
  /// <returns>The entry for the shape: the given one, or the one a concurrent translation put there first.</returns>
  /// <remarks>Concurrent translations of one shape produce equal entries, so the first in wins and nothing is locked.
  /// </remarks>
  public QueryCacheEntry AddEntry(ulong key, QueryCacheEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    var kept = _entries.GetOrAdd(key, entry);
    // A miss is counted only where the server will answer; see Misses.
    if (kept.CacheQuery == null)
      Interlocked.Increment(ref _misses);
    return kept;
  }

  public void Clear() {
    _entries.Clear();
    Interlocked.Exchange(ref _hits, 0);
    Interlocked.Exchange(ref _misses, 0);
  }

  public override string ToString() => $"{Count} shapes, {Hits} hits, {Misses} translations";
}
