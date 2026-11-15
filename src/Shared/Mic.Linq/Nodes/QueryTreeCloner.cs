namespace Mic.Linq.Nodes;

/// <summary>Copies a subtree: every node under a root once, with the references between the copies as they were
/// between the originals, and with the nodes the caller replaces standing in for their originals.</summary>
/// <remarks>A tree is a graph where a node is referenced from several places: a row from every column read off it, a
/// lambda's parameter from every mention, a common table expression from its own recursive step. The cloner maps each
/// original to its clone once, so the copy has the same shape of sharing. A node holds references in two places: its
/// children, which the cloner copies, and the links it keeps beside them, such as a column's row or a select's result
/// set, which each node class points at the clones in <see cref="QueryNode.RelinkClone"/>. A reference to a node
/// outside the subtree is kept as it is, unless the caller replaces it; a row is never copied on its own but is the
/// row of its rowset's clone.</remarks>
public sealed class QueryTreeCloner {
  /// <summary>Gets the clone the last <see cref="Clone"/> made, or <see langword="null"/> before the first.</summary>
  public QueryNode? Result { get; private set; }

  readonly Dictionary<QueryNode, QueryNode> _clones = new(ReferenceEqualityComparer.Instance);
  readonly HashSet<QueryNode> _inside = new(ReferenceEqualityComparer.Instance);
  readonly Func<QueryNode, QueryNode?>? _replaceFunc;

  /// <summary>Initializes a new instance of the <see cref="QueryTreeCloner"/> class.</summary>
  /// <param name="replaceFunc">A function that names the node to put in place of an original, for the original and
  /// for every reference to it, or returns <see langword="null"/> to have the original copied; or
  /// <see langword="null"/> to copy everything.</param>
  public QueryTreeCloner(Func<QueryNode, QueryNode?>? replaceFunc = null) {
    _replaceFunc = replaceFunc;
  }

  /// <summary>Clones the subtree under a node.</summary>
  /// <param name="root">The root of the subtree.</param>
  /// <returns>The clone's root, also kept in <see cref="Result"/>.</returns>
  public QueryNode Clone(QueryNode root) {
    ArgumentNullException.ThrowIfNull(root);
    Collect(root);
    Result = Map(root);
    return Result;
  }

  /// <summary>Gets the node that stands for an original in the clone: the replacement the caller named for it, the clone of a node
  /// inside the subtree, made on the first ask, or the node itself where it stands outside the subtree.</summary>
  /// <typeparam name="T">The type of the node.</typeparam>
  /// <param name="node">The original.</param>
  /// <returns>The node that stands for the original.</returns>
  public T Map<T>(T node) where T : QueryNode => (T)MapNode(node);

  /// <summary>Gets the node that stands for an original that may be absent.</summary>
  /// <typeparam name="T">The type of the node.</typeparam>
  /// <param name="node">The original, or <see langword="null"/>.</param>
  /// <returns>The node that stands for the original, or <see langword="null"/>.</returns>
  public T? MapOrNull<T>(T? node) where T : QueryNode => node == null ? null : Map(node);

  /// <summary>Gets the nodes that stand for a list of originals, as a new list.</summary>
  /// <typeparam name="T">The type of the nodes.</typeparam>
  /// <param name="nodes">The originals.</param>
  /// <returns>A new list of the nodes that stand for them, in order.</returns>
  public List<T> MapAll<T>(IEnumerable<T> nodes) where T : QueryNode {
    var mapped = new List<T>();
    foreach (var node in nodes)
      mapped.Add(Map(node));
    return mapped;
  }

  private QueryNode MapNode(QueryNode node) {
    if (_clones.TryGetValue(node, out var known))
      return known;
    if (_replaceFunc?.Invoke(node) is { } replacement) {
      _clones[node] = replacement;
      return replacement;
    }
    // A row is its rowset's: whatever the rowset becomes, the row is that rowset's row.
    if (node is RowNode row) {
      var rowSet = Map(row.RowSet);
      return ReferenceEquals(rowSet, row.RowSet) ? row : rowSet.Row;
    }
    if (!_inside.Contains(node))
      return node;
    return Copy(node);
  }

  /// <summary>Copies a node inside the subtree: the node's own fields, then its children, then its links.</summary>
  /// <remarks>The clone is recorded before its children are copied, so that a child reaching back up, as a recursive
  /// step reaches its common table expression, finds the clone.</remarks>
  private QueryNode Copy(QueryNode node) {
    var clone = node.CopyNode();
    _clones[node] = clone;
    if (node.HasChildren)
      foreach (var child in node.Children)
        clone.Children.Add(Map(child));
    clone.RelinkClone(this);
    return clone;
  }

  private void Collect(QueryNode node) {
    if (!_inside.Add(node) || !node.HasChildren)
      return;
    foreach (var child in node.Children)
      Collect(child);
  }
}
