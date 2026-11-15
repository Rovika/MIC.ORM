using System.Linq.Expressions;
using System.Reflection;
using Mic.Common;
using Mic.Linq.Model;
using Mic.Linq.Translation;

namespace Mic.Linq.Nodes;

/// <summary>Represents one expression of a query before anything has been decided about what it means, as both front
/// ends produce it.</summary>
/// <remarks>A node says what was written and no more: no entity is bound, no member is bound to the model and no
/// vendor is consulted until translation. The name is the key on a call and on a member alike; the typed path also
/// keeps the CLR member or method and the source expression, which a refusal quotes. Children always live in the
/// base's list, and a derived class exposes them by name.</remarks>
public class ExpressionNode : QueryNode {
  /// <summary>Initializes a new instance of the <see cref="ExpressionNode"/> class.</summary>
  /// <param name="nodeType">The kind of expression the node was.</param>
  /// <param name="dataType">The type of the value the node produces, or <see langword="null"/> if not known.</param>
  /// <param name="source">The expression the node came from, or <see langword="null"/> for one built by name.</param>
  public ExpressionNode(ExpressionType nodeType, Type? dataType = null, Expression? source = null)
      : base(NodeKind.Expression) {
    NodeType = nodeType;
    DataType = dataType;
    Source = source;
  }

  /// <summary>Gets or sets the kind of expression the node was.</summary>
  public ExpressionType NodeType { get; set; }

  /// <summary>Gets the expression the node came from, or <see langword="null"/> for one built by name.</summary>
  public Expression? Source { get; }

  /// <summary>Folds the expression kind into the cache key, since every node of this class has one
  /// <see cref="QueryNode.Kind"/>.</summary>
  /// <param name="hash">The hash so far.</param>
  /// <returns>The hash with the expression kind folded in.</returns>
  protected internal override ulong AddCustomNodeHash(ulong hash) => Fnv1aHash.Combine(hash, (ulong)NodeType);

  /// <inheritdoc/>
  public override string ToString() => Name is { Length: > 0 } ? $"{Kind} {NodeType} {Name}" : $"{Kind} {NodeType}";
}

/// <summary>Represents a field or property access by name and, once bound, the model member it names.</summary>
/// <remarks>The member is not bound to the model at construction. A bound member is this same class with
/// <see cref="QueryNode.Kind"/> changed to <see cref="NodeKind.Member"/>, <see cref="ModelMember"/> set and
/// <see cref="TargetEntity"/> naming what a reference reaches. The first child is what the member is read from.
/// </remarks>
public sealed class MemberAccessNode : ExpressionNode {
  /// <summary>Initializes a new instance of the <see cref="MemberAccessNode"/> class.</summary>
  /// <param name="memberName">The name of the member.</param>
  /// <param name="clrMember">The CLR member the typed path recorded, or <see langword="null"/> for one written by
  /// name.</param>
  /// <param name="dataType">The type of the value, or <see langword="null"/> if not known.</param>
  /// <param name="source">The expression the node came from, or <see langword="null"/> for one built by name.</param>
  public MemberAccessNode(string memberName, MemberInfo? clrMember = null, Type? dataType = null,
      Expression? source = null)
      : base(ExpressionType.MemberAccess, dataType, source) {
    ArgumentException.ThrowIfNullOrEmpty(memberName);
    MemberName = memberName;
    Name = memberName;
    ClrMember = clrMember;
  }

  /// <summary>Gets the name the member is bound against the model by.</summary>
  /// <remarks>This is not <see cref="QueryNode.Name"/>, which is the slot a projection returns the member under and
  /// which renaming does not rebind.</remarks>
  public string MemberName { get; }

  /// <summary>Gets the CLR member the typed path recorded, such as <c>Nullable&lt;&gt;.HasValue</c> or
  /// <c>string.Length</c>, or <see langword="null"/> for a member written by name.</summary>
  public MemberInfo? ClrMember { get; }

  /// <summary>Gets or sets the model member the access binds to, or <see langword="null"/> while unbound.</summary>
  public ILinqEntityMember? ModelMember { get; set; }

  /// <summary>Gets or sets what the member produces rows of, once bound: the entity a reference reaches, the shape
  /// of a document's rows, a grouping's key; or <see langword="null"/> for a value or while unbound.</summary>
  public ILinqEntity? Entity { get; set; }

  /// <summary>Gets what the member is read from, or <see langword="null"/> for a static member.</summary>
  public QueryNode? Target => ChildCount > 0 ? Children[0] : null;

  /// <summary>Gets the entity the member is looked up on, which is the target's, or <see langword="null"/> if the
  /// target has none.</summary>
  public ILinqEntity? OwnerEntity => Target == null ? null : Translation.TranslationHelper.GetEntity(Target);

  /// <summary>Gets the entity a reference member reaches, or <see langword="null"/> for a value.</summary>
  public ILinqEntity? TargetEntity => ModelMember?.GetTargetEntity();

  /// <summary>Folds the bound member's entity and member names into the cache key, so that two models of the same
  /// shape key alike.</summary>
  /// <param name="hash">The hash so far.</param>
  /// <returns>The hash with the member's place in the model folded in.</returns>
  protected internal override ulong AddCustomNodeHash(ulong hash) {
    hash = base.AddCustomNodeHash(hash);
    return ModelMember is { } model
      ? Fnv1aHash.Combine(Fnv1aHash.Combine(hash, model.Entity?.Name ?? ""), model.Name)
      : hash;
  }

  /// <inheritdoc/>
  public override string ToString() => ModelMember is { } model ? $"{model.Entity?.Name}.{model.Name}"
    : ClrMember is { } clr ? $"{clr.DeclaringType?.Name}.{clr.Name}" : $".{MemberName}";
}

/// <summary>Represents a method call by name, such as <c>Where</c>, <c>StartsWith</c> or <c>Abs</c>.</summary>
/// <remarks>The declaring type tells a query operation from a call on a row's list, and the return type is what an
/// aggregate answers as; the typed path reads both off <see cref="Method"/>, and the untyped path states them. The
/// instance of an instance call is the first child, so <see cref="Receiver"/> answers for an instance method and an
/// extension method alike.</remarks>
public sealed class MethodCallNode : ExpressionNode {
  /// <summary>Initializes a new instance of the <see cref="MethodCallNode"/> class.</summary>
  /// <param name="methodName">The name of the method.</param>
  /// <param name="method">The method the typed path recorded, or <see langword="null"/> for a call written by name.
  /// </param>
  /// <param name="dataType">The type of the value, or <see langword="null"/> if not known.</param>
  /// <param name="source">The expression the node came from, or <see langword="null"/> for one built by name.</param>
  public MethodCallNode(string methodName, MethodInfo? method = null, Type? dataType = null,
      Expression? source = null)
      : base(ExpressionType.Call, dataType, source) {
    ArgumentException.ThrowIfNullOrEmpty(methodName);
    MethodName = methodName;
    Name = methodName;
    Method = method;
    DeclaringType = method?.DeclaringType;
    ReturnType = method?.ReturnType;
  }

  /// <summary>Gets the name of the operation.</summary>
  /// <remarks>This is not <see cref="QueryNode.Name"/>, which is the slot an aggregate is returned under.</remarks>
  public string MethodName { get; }

  /// <summary>Gets the method the typed path recorded, or <see langword="null"/> for a call written by name.</summary>
  public MethodInfo? Method { get; }

  /// <summary>Gets or initializes the type the method is declared on, such as <c>Queryable</c>, <c>Enumerable</c>,
  /// <c>string</c> or <c>Math</c>.</summary>
  public Type? DeclaringType { get; init; }

  /// <summary>Gets or initializes the type the call produces, where that is known independently of its arguments.
  /// </summary>
  public Type? ReturnType { get; init; }

  /// <summary>Gets or initializes a value that indicates whether the call is written on an instance, as
  /// <c>text.StartsWith(…)</c> is.</summary>
  public bool HasInstance { get; init; }

  /// <summary>Gets or sets the entity a queryable call produces rows of, once the tree is bound against the
  /// model, or <see langword="null"/> while unbound and for a call that produces a value.</summary>
  public ILinqEntity? Entity { get; set; }

  /// <summary>Gets the number of arguments, not counting the instance.</summary>
  /// <remarks>The instance of an instance call is child 0, so a child index is an argument index only for a static
  /// call.</remarks>
  public int ArgumentCount => ChildCount - (HasInstance ? 1 : 0);

  /// <summary>Gets an argument by position, not counting the instance.</summary>
  /// <param name="index">The zero-based index of the argument.</param>
  /// <returns>The argument.</returns>
  public QueryNode Argument(int index) => Children[index + (HasInstance ? 1 : 0)];

  /// <summary>Gets the receiver of the call: the instance of an instance method, or argument zero of an extension
  /// method; <see langword="null"/> if there is neither.</summary>
  public QueryNode? Receiver => HasInstance ? Children[0] : ArgumentCount > 0 ? Argument(0) : null;

  /// <summary>Gets the arguments after the receiver, which are the children after the first for either spelling.
  /// </summary>
  public IReadOnlyList<QueryNode> ReceiverArguments => ChildCount <= 1 ? [] : Children.Skip(1).ToList();

  /// <summary>Folds the declaring type and the target entity into the cache key, since a call of the same name over a
  /// query and over a row's list, or a mutation aimed at two entities, are different statements.</summary>
  /// <param name="hash">The hash so far.</param>
  /// <returns>The hash with the declaring type and the entity folded in.</returns>
  protected internal override ulong AddCustomNodeHash(ulong hash) =>
    Fnv1aHash.Combine(Fnv1aHash.Combine(base.AddCustomNodeHash(hash), DeclaringType?.Name ?? ""), Entity?.Name ?? "");

  /// <inheritdoc/>
  public override string ToString() => $"{DeclaringType?.Name}.{MethodName}(…)";
}

/// <summary>Represents a binary operator, with <see cref="ExpressionNode.NodeType"/> saying which; the first child is
/// the left operand and the second the right.</summary>
/// <remarks>A bound comparison or arithmetic operation is this same class with <see cref="QueryNode.Kind"/> changed
/// to <see cref="NodeKind.Binary"/>; binding settles only its result type. <c>&amp;&amp;</c> and <c>||</c> arrive as
/// this class and bind to a <see cref="LogicalOpNode"/>.</remarks>
public sealed class BinaryNode : ExpressionNode {
  /// <summary>Initializes a new instance of the <see cref="BinaryNode"/> class.</summary>
  /// <param name="nodeType">The operator.</param>
  /// <param name="dataType">The type of the value, or <see langword="null"/> if not known.</param>
  /// <param name="source">The expression the node came from, or <see langword="null"/> for one built by name.</param>
  public BinaryNode(ExpressionType nodeType, Type? dataType = null, Expression? source = null)
      : base(nodeType, dataType, source) { }

  /// <summary>Gets the left operand.</summary>
  public QueryNode Left => Children[0];
  /// <summary>Gets the right operand.</summary>
  public QueryNode Right => Children[1];

  /// <summary>Gets a value that indicates whether the operator is a comparison, which produces a truth value.</summary>
  public bool IsComparison => NodeType is ExpressionType.Equal or ExpressionType.NotEqual
    or ExpressionType.LessThan or ExpressionType.LessThanOrEqual
    or ExpressionType.GreaterThan or ExpressionType.GreaterThanOrEqual;

  /// <summary>Gets a value that indicates whether the operator is arithmetic, checked or not.</summary>
  public bool IsArithmetic => NodeType is ExpressionType.Add or ExpressionType.AddChecked
    or ExpressionType.Subtract or ExpressionType.SubtractChecked
    or ExpressionType.Multiply or ExpressionType.MultiplyChecked or ExpressionType.Divide;

  /// <inheritdoc/>
  public override string ToString() => $"{NodeType}";
}

/// <summary>Represents a lambda, whose parameters come first among the children and whose body comes last.</summary>
public sealed class LambdaNode : ExpressionNode {
  /// <summary>Initializes a new instance of the <see cref="LambdaNode"/> class.</summary>
  /// <param name="parameterCount">The number of parameters.</param>
  /// <param name="dataType">The type of the value, or <see langword="null"/> if not known.</param>
  /// <param name="source">The expression the node came from, or <see langword="null"/> for one built by name.</param>
  public LambdaNode(int parameterCount, Type? dataType = null, Expression? source = null)
      : base(ExpressionType.Lambda, dataType, source) => ParameterCount = parameterCount;

  /// <summary>Gets the number of parameters.</summary>
  public int ParameterCount { get; }

  /// <summary>Gets the body the lambda evaluates to.</summary>
  public QueryNode Body => Children[ParameterCount];
}

/// <summary>Represents a constructed value, such as <c>new { p.Rank, c.Name }</c>, <c>new Row(a, b)</c> or the slots
/// of an untyped <c>Select</c>, with its slot names.</summary>
/// <remarks>Both a projection's column names and a transparent identifier's row names come from the slot names. A
/// <see langword="null"/> slot name means the slot has no name of its own; a member keeps its own name, and any
/// other unnamed slot is refused where the projection is built.</remarks>
public sealed class NewNode : ExpressionNode {
  /// <summary>Initializes a new instance of the <see cref="NewNode"/> class.</summary>
  /// <param name="slotNames">One name per argument, with <see langword="null"/> where the constructor named none; or
  /// <see langword="null"/> if no slot is named.</param>
  /// <param name="dataType">The type of the value, or <see langword="null"/> if not known.</param>
  /// <param name="source">The expression the node came from, or <see langword="null"/> for one built by name.</param>
  public NewNode(IReadOnlyList<string?>? slotNames, Type? dataType = null, Expression? source = null)
      : base(ExpressionType.New, dataType, source) => SlotNames = slotNames;

  /// <summary>Gets one name per argument, with <see langword="null"/> where the constructor named none; or
  /// <see langword="null"/> if no slot is named.</summary>
  public IReadOnlyList<string?>? SlotNames { get; }
}

/// <summary>Represents a lambda's parameter, such as <c>row</c> in <c>row => …</c>: the row the query is written
/// over, not a value it is given.</summary>
/// <remarks>Every mention of the parameter inside the lambda is the same node, which is the identity the engine binds
/// on; a query parameter is a <see cref="QueryParameterNode"/>.</remarks>
public sealed class LambdaParameterNode : ExpressionNode {
  /// <summary>Initializes a new instance of the <see cref="LambdaParameterNode"/> class.</summary>
  /// <param name="name">The name of the parameter, or <see langword="null"/> for an unnamed one.</param>
  /// <param name="dataType">The type of the value, or <see langword="null"/> if not known.</param>
  /// <param name="source">The expression the node came from, or <see langword="null"/> for one built by name.</param>
  public LambdaParameterNode(string? name, Type? dataType = null, Expression? source = null)
      : base(ExpressionType.Parameter, dataType, source) => Name = name;

  /// <summary>Gets or sets the entity the parameter is a row of, once the tree is bound to the model, or
  /// <see langword="null"/> while unbound.</summary>
  public ILinqEntity? Entity { get; set; }

  /// <summary>Gets or sets the node whose rows the parameter stands for: its call's source, a join's inner rowset,
  /// a collection selector's body; or <see langword="null"/> for a parameter that is a value, such as a recursive
  /// query's level, or before the binder has linked it.</summary>
  /// <remarks>A link, not a child: the node is a sibling subtree of the lambda, bound before the parameter is.
  /// </remarks>
  public QueryNode? RowSource { get; set; }

  /// <summary>Gets or sets a value that indicates whether the parameter is a row of the rows its source groups
  /// rather than a group: an aggregate's predicate or selector over a grouping is written over the grouped rows.
  /// </summary>
  public bool OverGroupedRows { get; set; }

  /// <inheritdoc/>
  protected internal override void RelinkClone(QueryTreeCloner cloner) => RowSource = cloner.MapOrNull(RowSource);

  /// <inheritdoc/>
  public override string ToString() => Name ?? "<parameter>";
}
