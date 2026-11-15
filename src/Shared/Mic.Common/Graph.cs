namespace Mic.Common;

/// <summary>Represents one group of nodes that can be done together, and whether they form a cycle. This class cannot
/// be inherited.</summary>
/// <remarks>A cycle cannot be ordered internally; what that means is the caller's to decide.</remarks>
/// <typeparam name="T">The type of a node.</typeparam>
public sealed class GraphComponent<T> {
  internal GraphComponent(IReadOnlyList<T> nodes, bool isCycle) {
    Nodes = nodes;
    IsCycle = isCycle;
  }

  public IReadOnlyList<T> Nodes { get; }

  /// <summary>Gets a value that indicates whether the nodes reference each other in a loop.</summary>
  /// <value><see langword="true"/> if the nodes form a cycle, which for a single node means it references itself;
  /// otherwise, <see langword="false"/>. A lone node with no edge to itself is not a cycle.</value>
  public bool IsCycle { get; }

  public override string ToString() =>
    $"[{string.Join(", ", Nodes)}]{(IsCycle ? " (cycle)" : "")}";
}

/// <summary>Represents a directed graph of <typeparamref name="T"/> nodes, built by adding nodes and edges, that
/// answers in what order its nodes can be done and which of them cannot be ordered at all. This class cannot be
/// inherited.</summary>
/// <remarks>The answer is the strongly connected components, topologically sorted, from
/// <see cref="GetSortedComponents"/>.</remarks>
/// <typeparam name="T">The type of a node.</typeparam>
public sealed class Graph<T> where T : notnull {
  sealed class Node(T value) {
    public readonly T Value = value;
    public readonly List<Node> Targets = [];
    public int Index = -1;
    public int LowLink;
    public bool OnStack;
    public bool SelfReferencing;
  }

  readonly Dictionary<T, Node> _nodes;
  readonly List<Node> _ordered = [];

  /// <summary>Initializes a new instance of the <see cref="Graph{T}"/> class.</summary>
  /// <param name="comparer">The comparer that identifies nodes, or <see langword="null"/> for the default comparer,
  /// which for a class type is reference identity.</param>
  public Graph(IEqualityComparer<T>? comparer = null) => _nodes = new Dictionary<T, Node>(comparer);

  public int NodeCount => _nodes.Count;

  /// <summary>Adds a node, if it is not there already.</summary>
  /// <param name="node">The node to add.</param>
  /// <remarks>Add a node even when it has no edges; a graph built only from edges does not contain it.</remarks>
  public void AddNode(T node) => GetOrAdd(node);

  /// <summary>Records that <paramref name="from"/> depends on <paramref name="to"/>, so that <paramref name="to"/>
  /// must be done first.</summary>
  /// <param name="from">The dependent node.</param>
  /// <param name="to">The node it depends on.</param>
  /// <remarks>An edge points from the dependent to what it depends on, and <see cref="GetSortedComponents"/> returns
  /// what is depended upon first. A record with a foreign key depends on the row it points at, so the edge runs from
  /// the child to the parent, and the parent comes back first.</remarks>
  public void AddEdge(T from, T to) {
    var source = GetOrAdd(from);
    var target = GetOrAdd(to);
    source.Targets.Add(target);
    if (ReferenceEquals(source, target))
      source.SelfReferencing = true;
  }

  Node GetOrAdd(T value) {
    if (_nodes.TryGetValue(value, out var node))
      return node;
    _nodes[value] = node = new Node(value);
    _ordered.Add(node);
    return node;
  }

  /// <summary>Gets the strongly connected components of the graph, dependencies first.</summary>
  /// <returns>A list of the components, each one before the components that depend on it.</returns>
  /// <remarks>The algorithm is iterative, so a chain of many thousand nodes referencing one another does not overflow
  /// the stack.</remarks>
  public IReadOnlyList<GraphComponent<T>> GetSortedComponents() {
    foreach (var node in _ordered) {
      node.Index = -1;
      node.OnStack = false;
    }

    var components = new List<GraphComponent<T>>();
    var connected = new Stack<Node>();          // Tarjan's stack: the nodes of a component not yet closed
    var work = new Stack<(Node Node, int Next)>();
    var next = 0;

    foreach (var start in _ordered) {
      if (start.Index >= 0)
        continue;

      Open(start);
      while (work.Count > 0) {
        var (node, edge) = work.Pop();

        if (edge < node.Targets.Count) {
          work.Push((node, edge + 1));
          var target = node.Targets[edge];
          if (target.Index < 0) {
            Open(target);
          } else if (target.OnStack) {
            node.LowLink = Math.Min(node.LowLink, target.Index);
          }
          continue;
        }

        // Every edge out of this node has been followed; carry its low link up to the node that reached it.
        if (work.Count > 0) {
          var (parent, _) = work.Peek();
          parent.LowLink = Math.Min(parent.LowLink, node.LowLink);
        }

        // A node whose low link is its own index is the root of its component; everything above it on the stack
        // belongs with it.
        if (node.LowLink != node.Index)
          continue;
        var members = new List<T>();
        Node popped;
        do {
          popped = connected.Pop();
          popped.OnStack = false;
          members.Add(popped.Value);
        } while (!ReferenceEquals(popped, node));

        components.Add(new GraphComponent<T>(members, members.Count > 1 || node.SelfReferencing));
      }
    }
    return components;

    void Open(Node node) {
      node.Index = node.LowLink = next++;
      node.OnStack = true;
      connected.Push(node);
      work.Push((node, 0));
    }
  }
}
