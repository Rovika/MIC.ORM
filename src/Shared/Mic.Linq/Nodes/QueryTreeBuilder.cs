using Mic.Linq.Translation;

namespace Mic.Linq.Nodes;

/// <summary>Builds a query's tree when its shape has to be translated: a root in hand, an expression converted, or a
/// standard query made by a function.</summary>
/// <remarks>A builder is asked to build only for a shape the query cache has not seen, and hands out a root whose
/// parameters are laid out. A function that returns a root in hand hands out that one root each time; the others
/// build a fresh tree.</remarks>
public sealed class QueryTreeBuilder {
  readonly Func<QueryNode> _build;

  /// <summary>Initializes a new instance of the <see cref="QueryTreeBuilder"/> class.</summary>
  /// <param name="build">A function that returns the root of the tree.</param>
  public QueryTreeBuilder(Func<QueryNode> build) {
    ArgumentNullException.ThrowIfNull(build);
    _build = build;
  }

  /// <summary>Builds the root of the tree, with its parameters laid out.</summary>
  /// <returns>The root of the tree.</returns>
  public QueryNode Build() {
    var root = _build();
    root.BuildParametersLayout();
    return root;
  }
}
