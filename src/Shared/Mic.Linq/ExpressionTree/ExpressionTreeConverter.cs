using System.Linq.Expressions;
using Mic.Common;
using Mic.Linq.Nodes;
namespace Mic.Linq.ExpressionTree;

/// <summary>Copies an expression tree into the engine's own mutable tree, one node per expression, deciding nothing
/// about what it means.</summary>
/// <remarks>A lambda parameter is one node however many times it is mentioned, which is the identity the engine
/// binds on. In an override, the order of the base call matters: decorate before recursing when the children need
/// to see it, and read the children after recursing when the decoration depends on them.</remarks>
public sealed class ExpressionTreeConverter : ExpressionWalker {
  readonly SmallLookup<ParameterExpression, ExpressionNode> _parameters = new();
  QueryNode? _current;
  QueryNode? _root;

  /// <summary>Converts an expression tree.</summary>
  /// <param name="expression">The expression of a typed query.</param>
  /// <returns>A tree of <see cref="ExpressionNode"/> and nothing else.</returns>
  public static QueryNode Convert(Expression expression) {
    ArgumentNullException.ThrowIfNull(expression);
    var converter = new ExpressionTreeConverter();
    converter.Walk(expression);
    return converter._root
      ?? throw new InvalidOperationException("An expression converted to nothing, which cannot happen.");
  }

  /// <summary>Makes a node for an expression, lets the walk fill its children, and hangs it on its parent.</summary>
  /// <param name="expression">The expression to walk, or <see langword="null"/> for nothing to do.</param>
  protected override void Walk(Expression? expression) {
    if (expression == null)
      return;
    var parent = _current;
    // An include's selector is kept as written, never walked; the query it was written on is the node's one child.
    if (expression.NodeType == ExpressionType.Call && expression is MethodCallExpression call && IncludeCallHelper.IsInclude(call.Method)) {
      _current = new IncludeNode([new QueryInclude(IncludeCallHelper.GetSelector(call))]);
      Walk(call.Arguments[0]);
    } else {
      _current = MakeNode(expression);
      base.Walk(expression);
    }

    // Read after the recursion, since an override may have replaced the current node.
    var built = _current;
    _current = parent;
    if (parent == null)
      _root = built;
    else
      parent.AddChild(built);
  }

  /// <summary>Makes one node for an expression, with the typed payload where there is one; a constant and an entity
  /// set come out as the nodes the untyped path makes.</summary>
  private static QueryNode MakeNode(Expression expression) => expression switch {
    ConstantExpression { Value: IRowSet { RowEntity: not null } set } => set.GetNode(),
    ConstantExpression constant => QueryMaker.Constant(constant.Value, constant.Type),
    BinaryExpression binary => new BinaryNode(binary.NodeType, binary.Type, binary),
    MemberExpression member => new MemberAccessNode(member.Member.Name, member.Member, member.Type, member),
    MethodCallExpression call =>
      new MethodCallNode(call.Method.Name, call.Method, call.Type, call) { HasInstance = call.Object != null },
    LambdaExpression lambda => new LambdaNode(lambda.Parameters.Count, lambda.Type, lambda),
    ParameterExpression parameter => new LambdaParameterNode(parameter.Name, parameter.Type, parameter),
    NewExpression create => new NewNode(GetSlotNames(create), create.Type, create),
    MemberInitExpression init => new NewNode(GetSlotNames(init), init.Type, init),
    _ => new ExpressionNode(expression.NodeType, expression.Type, expression),
  };

  /// <summary>Gets one name per constructor argument: the property it initialises for an anonymous type or a
  /// positional record, the parameter's own name for a plain constructor, or <see langword="null"/> where neither is
  /// known.</summary>
  private static IReadOnlyList<string?>? GetSlotNames(NewExpression create) =>
    create.Members is { } members ? [.. members.Select(m => (string?)m.Name)]
    : create.Constructor is { } constructor ? [.. constructor.GetParameters().Select(p => (string?)p.Name)]
    : null;

  /// <summary>Gets the slot names of an object initializer: the constructor's names, then one per assignment, in the
  /// order the walk visits them.</summary>
  /// <remarks>A nested or list initializer is refused, since no projection has a column for one.</remarks>
  private static IReadOnlyList<string?> GetSlotNames(MemberInitExpression init) {
    var names = new List<string?>(GetSlotNames(init.NewExpression) ?? []);
    foreach (var binding in init.Bindings)
      names.Add(binding is MemberAssignment assignment
        ? assignment.Member.Name
        : throw new NotSupportedException(
          $"A '{binding.BindingType}' initializer cannot be part of a query — only `Member = value` can be selected."));
    return names;
  }

  /// <summary>Converts a member access; a local variable holding a query is converted as the query's expression in
  /// place of the member that reads it.</summary>
  /// <param name="node">The member access.</param>
  /// <remarks>A captured query is the only form of sub-query: a call that starts a query inside a lambda names no
  /// set without running.</remarks>
  protected override void WalkMember(MemberExpression node) {
    if (!LocalQueryHelper.TryGetQuery(node, out var query)) {
      base.WalkMember(node);
      return;
    }
    Walk(query.Expression);
    // What was built under the member node stands in for it.
    _current = _current!.Children[0];
  }

  /// <summary>Converts a lambda, whose parameters are nodes of its own: a captured query copied into two places has
  /// the same lambda twice, and each copy gets its own parameters.</summary>
  /// <param name="node">The lambda.</param>
  protected override void WalkLambda(LambdaExpression node) {
    foreach (var parameter in node.Parameters)
      _parameters[parameter] = null!;
    base.WalkLambda(node);
  }

  /// <summary>Converts a lambda parameter; one met twice within its lambda is the node made the first time.</summary>
  /// <param name="node">The parameter.</param>
  protected override void WalkParameter(ParameterExpression node) {
    if (_parameters.TryGetValue(node, out var already) && already != null)
      _current = already;
    else if (_current is LambdaParameterNode made)
      _parameters[node] = made;
  }

  /// <summary>Converts a unary expression, dropping the quote the compiler wraps a lambda in.</summary>
  /// <param name="node">The unary expression.</param>
  protected override void WalkUnary(UnaryExpression node) {
    base.WalkUnary(node);
    if (node.NodeType == ExpressionType.Quote && _current is { ChildCount: 1 })
      _current = _current.Children[0];
  }
}
