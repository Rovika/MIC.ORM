using Mic.Common;
using Mic.Linq.Model;

namespace Mic.Linq.Nodes;

/// <summary>Represents one node of a query tree: a mutable node of a kind, with its children in one list, which the
/// translation passes transform in place.</summary>
/// <remarks>A tree is scratch for one translation; nothing outlives a translation but the finished statement or
/// evaluator. Anything that contributes a fragment to many queries, such as an extension's filter, hands each query
/// a <see cref="Clone"/> of a prepared subtree, never the subtree itself, since translation writes onto every node it
/// walks. Equality operators are not overloaded: equality is written as <c>Equal</c>, and conjunction as <c>&amp;</c>, which
/// binds after the comparison operators.</remarks>
public class QueryNode {
  /// <summary>Gets the children of the node, allocated on first use.</summary>
  /// <remarks>A pass replaces a child by assigning to the list.</remarks>
  public List<QueryNode> Children => _children ??= [];

  /// <summary>Gets the number of children.</summary>
  public int ChildCount => _children?.Count ?? 0;

  /// <summary>Gets a value that indicates whether the node has any children.</summary>
  public bool HasChildren => _children is { Count: > 0 };

  /// <summary>Gets the child at a position.</summary>
  /// <param name="index">The zero-based index of the child.</param>
  /// <returns>The child, or <see langword="null"/> if there is none at that position.</returns>
  /// <remarks>Each kind's child layout is documented on <see cref="NodeKind"/>. A missing child is tolerated so that
  /// a half-built tree can still be printed.</remarks>
  public QueryNode? ChildAt(int index) => index < ChildCount ? Children[index] : null;

  /// <summary>Adds a child after the existing ones.</summary>
  /// <param name="child">The child to add.</param>
  /// <returns>This node.</returns>
  public QueryNode AddChild(QueryNode child) {
    Children.Add(child);
    return this;
  }

  List<QueryNode>? _children;
  ulong _cacheKey;

  /// <summary>Initializes a new instance of the <see cref="QueryNode"/> class with no children.</summary>
  /// <param name="kind">One of the enumeration values that specifies what the node is.</param>
  public QueryNode(NodeKind kind) { Kind = kind; }

  /// <summary>Initializes a new instance of the <see cref="QueryNode"/> class with children.</summary>
  /// <param name="kind">One of the enumeration values that specifies what the node is.</param>
  /// <param name="children">An array that contains the children, in order.</param>
  public QueryNode(NodeKind kind, params QueryNode[] children) {
    Kind = kind;
    if (children.Length > 0)
      Children.AddRange(children);
  }

  /// <summary>Gets or sets what the node is.</summary>
  public NodeKind Kind { get; set; }
  /// <summary>Gets or sets a bitwise combination of the enumeration values that describe the node beyond its kind.
  /// </summary>
  public NodeFlags Flags { get; set; }
  /// <summary>Gets or sets where the node's work happens.</summary>
  public QueryTier Tier { get; set; }
  /// <summary>Gets or sets what the translation has learned about the node beside its tier, such as that its subtree
  /// runs in memory over cached entities.</summary>
  public TranslationFlags TranslationFlags { get; set; }

  /// <summary>Gets or sets what the node names, such as a member's name or a parameter's, or <see langword="null"/>
  /// if the node names nothing.</summary>
  public string? Name { get; set; }

  /// <summary>Gets or sets the CLR type of the value the node produces, or <see langword="null"/> if it produces
  /// none.</summary>
  /// <remarks>The type is <see langword="null"/> for every node that produces rows, and for a member that is a
  /// reference or a list, by the rule <see cref="ILinqEntityMember.DataType"/> holds to. Later passes fill in the
  /// type where only they can work it out.</remarks>
  public Type? DataType { get; set; }

  /// <summary>When overridden in a derived class, folds the facts the derived class adds into the node's cache key.
  /// </summary>
  /// <param name="hash">The hash so far, with the kind, the flags and the name folded in.</param>
  /// <returns>The hash with the derived class's facts folded in.</returns>
  /// <remarks>A derived class with a fact of its own that changes the statement must fold it here, or two different
  /// nodes key alike.</remarks>
  protected internal virtual ulong AddCustomNodeHash(ulong hash) => hash;

  /// <summary>Gets or sets the cache key of the subtree once computed, or zero until then.</summary>
  internal ulong CacheKey { get => _cacheKey; set => _cacheKey = value; }

  /// <summary>Returns a copy of the subtree under this node, which another tree may take and write on.</summary>
  /// <param name="replaceFunc">A function that names the node to put in place of an original, for the original and
  /// for every reference to it, or returns <see langword="null"/> to have the original copied; or
  /// <see langword="null"/> to copy everything. Used to stand a prepared subtree over a different row.</param>
  /// <returns>The copy's root.</returns>
  /// <remarks>Every node inside the subtree is copied once, so what was shared is shared in the copy; a reference to
  /// a node outside the subtree is kept, unless replaced. See <see cref="QueryTreeCloner"/>.</remarks>
  public QueryNode Clone(Func<QueryNode, QueryNode?>? replaceFunc = null) => new QueryTreeCloner(replaceFunc).Clone(this);

  /// <summary>When overridden in a derived class, returns a copy of this node alone: its own fields, and no children.
  /// </summary>
  /// <returns>The copy.</returns>
  /// <remarks>The base copies every field as it is, so a list or a node the derived class holds is still the
  /// original's; the derived class gives the copy its own in <see cref="RelinkClone"/>, or here where the copy needs
  /// it before its children are copied, as a rowset needs its row.</remarks>
  protected internal virtual QueryNode CopyNode() {
    var copy = (QueryNode)MemberwiseClone();
    copy._children = null;
    return copy;
  }

  /// <summary>When overridden in a derived class, points the references this copy holds beside its children, such
  /// as a column's row or a select's result set, at the nodes that stand for them in the copy.</summary>
  /// <param name="cloner">The cloner, which answers the node that stands for any original.</param>
  /// <remarks>Called on the copy once its children are copied. A derived class that adds a field referencing a node
  /// adds the line that maps it here, or the copy points back into the original tree.</remarks>
  protected internal virtual void RelinkClone(QueryTreeCloner cloner) { }

  /// <summary>Gets the whole subtree as readable text, such as <c>Take(OrderBy(Where(Employee, …), …), 20)</c>.
  /// </summary>
  /// <remarks>The text is produced by <see cref="QueryNodeTextHelper"/>, for a test, a log line or a debugger
  /// watch.</remarks>
  public virtual string AsText => QueryNodeTextHelper.GetText(this);

  /// <summary>Returns the kind and the name of the node, on one line and without the subtree.</summary>
  /// <returns>The kind and the name.</returns>
  public override string ToString() => Name is { Length: > 0 } ? $"{Kind} {Name}" : Kind.ToString();

  // ----- writing a query by name, in C#'s own signs ---------------------------------------------------------------------

  /// <summary>Gets a member of the row by name, such as <c>row["Shop"]["Name"]</c>.</summary>
  /// <param name="memberName">The name of the member.</param>
  /// <returns>A member access node over this node.</returns>
  public QueryNode this[string memberName] => QueryMaker.Member(this, memberName);

  public static QueryNode operator <(QueryNode left, QueryNode right) => QueryMaker.Less(left, right);
  public static QueryNode operator >(QueryNode left, QueryNode right) => QueryMaker.Greater(left, right);
  public static QueryNode operator <=(QueryNode left, QueryNode right) => QueryMaker.LessOrEqual(left, right);
  public static QueryNode operator >=(QueryNode left, QueryNode right) => QueryMaker.GreaterOrEqual(left, right);

  public static QueryNode operator +(QueryNode left, QueryNode right) => QueryMaker.Add(left, right);
  public static QueryNode operator -(QueryNode left, QueryNode right) => QueryMaker.Subtract(left, right);
  public static QueryNode operator *(QueryNode left, QueryNode right) => QueryMaker.Multiply(left, right);
  public static QueryNode operator /(QueryNode left, QueryNode right) => QueryMaker.Divide(left, right);

  public static QueryNode operator &(QueryNode left, QueryNode right) => QueryMaker.And(left, right);
  public static QueryNode operator |(QueryNode left, QueryNode right) => QueryMaker.Or(left, right);
  public static QueryNode operator !(QueryNode operand) => QueryMaker.Not(operand);

  // A bare value converts to a parameter, never to a constant: a constant is written into the statement and its cache
  // key, which would make an entry per value. Where a constant is meant it is written as Constant(…).
  public static implicit operator QueryNode(int value) => QueryMaker.MakeValue(typeof(int), value);
  public static implicit operator QueryNode(long value) => QueryMaker.MakeValue(typeof(long), value);
  public static implicit operator QueryNode(decimal value) => QueryMaker.MakeValue(typeof(decimal), value);
  public static implicit operator QueryNode(double value) => QueryMaker.MakeValue(typeof(double), value);
  public static implicit operator QueryNode(bool value) => QueryMaker.MakeValue(typeof(bool), value);
  public static implicit operator QueryNode(string value) => QueryMaker.MakeValue(typeof(string), value);
  public static implicit operator QueryNode(Guid value) => QueryMaker.MakeValue(typeof(Guid), value);
  public static implicit operator QueryNode(DateTime value) => QueryMaker.MakeValue(typeof(DateTime), value);
  public static implicit operator QueryNode(DateOnly value) => QueryMaker.MakeValue(typeof(DateOnly), value);
}
