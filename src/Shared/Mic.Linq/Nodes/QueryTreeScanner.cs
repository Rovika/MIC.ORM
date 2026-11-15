using Mic.Linq.Translation;

namespace Mic.Linq.Nodes;

/// <summary>Scans a query built by name: lays out its parameters, hashes its key, and gathers the arguments against
/// the layout.</summary>
public static class QueryTreeScanner {
  /// <summary>Scans a tree whose root is in hand.</summary>
  /// <param name="root">The root of the tree.</param>
  /// <param name="arguments">The caller's values for the parameters the query declares, in slot order; a parameter
  /// made with its value needs none. <see langword="null"/> for a tree that is translated and never run.</param>
  /// <param name="options">A bitwise combination of the enumeration values that specifies what the caller asks for
  /// beyond the query itself.</param>
  /// <returns>The query's info: its cache key, its arguments one per slot, its options and its parameters' layout.
  /// </returns>
  /// <remarks>The layout is built before the key, since a parameter made from a bare value is named by its slot and
  /// the name is part of the key.</remarks>
  public static QueryInfo Scan(QueryNode root, IReadOnlyList<object?>? arguments = null,
      QueryOptions options = QueryOptions.None) {
    ArgumentNullException.ThrowIfNull(root);
    var layout = root.BuildParametersLayout();
    var cacheKey = root.GetCacheKey();
    return new QueryInfo(cacheKey, GatherArguments(layout, arguments), options, parameters: layout);
  }

  /// <summary>Gathers one argument per slot: the value on the node where the parameter was made with one, otherwise
  /// the caller's next; the count is checked exactly.</summary>
  private static IReadOnlyList<object?> GatherArguments(IReadOnlyList<QueryParameterNode> layout, IReadOnlyList<object?>? given) {
    if (given == null)
      return [];
    var declared = 0;
    foreach (var parameter in layout)
      if (parameter.ArgValue == null)
        declared++;
    if (declared == layout.Count)
      return given;                  // nothing carried: the caller's list is counted where it is bound
    if (given.Count != declared)
      throw new ArgumentException(
        $"The query declares {declared} parameter(s) to be given a value and {given.Count} were given. A collection "
        + "passed as one argument has to be wrapped — `[values]`, not `values` — or it spreads into one argument per "
        + "element.", nameof(given));
    var values = new object?[layout.Count];
    var nextGiven = 0;
    for (var i = 0; i < values.Length; i++)
      values[i] = layout[i].ArgValue is { } box ? box.Value : given[nextGiven++];
    return values;
  }
}
