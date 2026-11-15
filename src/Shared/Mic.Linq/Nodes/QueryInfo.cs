using Mic.Common;
using Mic.Linq.ExpressionTree;

namespace Mic.Linq.Nodes;

/// <summary>Represents what identifies one query and what it carries: the cache key of its shape, its arguments, its
/// options, and the set a typed query starts from.</summary>
/// <remarks>The key and the arguments are what a query cache hit needs; the tree is built only for a shape the cache
/// has not seen, through a <see cref="QueryTreeBuilder"/>.</remarks>
public sealed class QueryInfo {
  /// <summary>Gets the query cache's key for the shape.</summary>
  public ulong CacheKey { get; }

  /// <summary>Gets the arguments, one per slot.</summary>
  /// <remarks>For a typed query these are the captured objects the scan found, possibly more of them than the
  /// statement binds, and a parameter reads its value out of one. For a query built by name they are values, in slot
  /// order: the one a parameter was made with, or the caller's.</remarks>
  public IReadOnlyList<object?> Arguments { get; }

  /// <summary>Gets the options the caller asked for beyond the query itself, merged across every source the query
  /// names.</summary>
  /// <remarks>Only the lock options are part of the cache key; see <see cref="QueryOptions"/>.</remarks>
  public QueryOptions Options { get; }

  /// <summary>Gets the set a typed query is rooted at, or <see langword="null"/> for a query built by name or a typed
  /// query whose scan met no set.</summary>
  public IRowSet? RootSet { get; }

  /// <summary>Gets a value that indicates whether the query came from a typed expression, so that its arguments are
  /// the scan's captured objects rather than values in slot order.</summary>
  public bool IsTyped { get; }

  /// <summary>Gets the parameters of a query built by name that take an argument slot, in slot order; or
  /// <see langword="null"/> for a typed query, which has no slots, and for a query under a fixed key, whose tree is
  /// built only when its shape is translated.</summary>
  public IReadOnlyList<QueryParameterNode>? Parameters { get; }

  /// <summary>Gets the number of argument slots the query's parameters take: zero for a typed query, the layout's
  /// count for a query built by name, and the argument count for a query under a fixed key, which declares every
  /// parameter.</summary>
  /// <remarks>The count may be more than a statement binds: a parameter named only inside a sub-query evaluated in
  /// memory still has its slot, and the caller still passes its value.</remarks>
  public int SlotCount => IsTyped ? 0 : Parameters?.Count ?? Arguments.Count;

  /// <summary>Initializes a new instance of the <see cref="QueryInfo"/> class.</summary>
  /// <param name="cacheKey">The query cache's key for the shape.</param>
  /// <param name="arguments">The arguments, one per slot.</param>
  /// <param name="options">A bitwise combination of the enumeration values that specifies what the caller asks for
  /// beyond the query itself.</param>
  /// <param name="rootSet">The set a typed query is rooted at, or <see langword="null"/>.</param>
  /// <param name="isTyped"><see langword="true"/> for a query that came from a typed expression; otherwise,
  /// <see langword="false"/>.</param>
  /// <param name="parameters">The parameters of a query built by name, in slot order, or
  /// <see langword="null"/>.</param>
  public QueryInfo(ulong cacheKey, IReadOnlyList<object?> arguments, QueryOptions options = QueryOptions.None,
      IRowSet? rootSet = null, bool isTyped = false, IReadOnlyList<QueryParameterNode>? parameters = null) {
    ArgumentNullException.ThrowIfNull(arguments);
    CacheKey = cacheKey;
    Arguments = arguments;
    Options = options;
    RootSet = rootSet;
    IsTyped = isTyped;
    Parameters = parameters;
  }

  /// <summary>Returns a copy with one more argument, last.</summary>
  /// <param name="extraArg">The argument to add.</param>
  /// <returns>A new query info with the same key, options, root set and kind.</returns>
  /// <remarks>The argument's position is the old count, the same for every call of the shape, so a parameter that
  /// reads it is bound at the same position every time.</remarks>
  public QueryInfo WithArgument(object extraArg) {
    ArgumentNullException.ThrowIfNull(extraArg);
    return new QueryInfo(CacheKey, [.. Arguments, extraArg], Options, RootSet, IsTyped, Parameters);
  }

  /// <summary>Returns the position of an object in <see cref="Arguments"/>.</summary>
  /// <param name="argument">The object to find, by reference.</param>
  /// <returns>The zero-based index of the argument, or -1 if it is not one.</returns>
  public int IndexOf(object? argument) {
    for (var i = 0; i < Arguments.Count; i++)
      if (ReferenceEquals(Arguments[i], argument))
        return i;
    return -1;
  }

  /// <inheritdoc/>
  public override string ToString() => $"{Fnv1aHash.ToHex(CacheKey)}, {Arguments.Count} arguments";
}
