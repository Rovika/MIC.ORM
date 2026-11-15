using System.Collections.ObjectModel;
using System.Linq.Expressions;
using Mic.Linq.Nodes;
using Mic.Linq.Translation;

namespace Mic.Linq.ExpressionTree;

/// <summary>Defines the core behavior of a walk over an expression tree that changes nothing, and provides a base for
/// the scanner and the converter.</summary>
/// <remarks>Every method returns void; the tree is never rebuilt. An expression kind the walk does not understand is
/// refused with an exception, never skipped.</remarks>
public abstract class ExpressionWalker {

  /// <summary>Walks one expression by dispatching to the method for its kind.</summary>
  /// <param name="expression">The expression to walk, or <see langword="null"/> for nothing to do.</param>
  protected virtual void Walk(Expression? expression) {
    switch (expression) {
      case null: return;
      case UnaryExpression unary: WalkUnary(unary); return;
      case BinaryExpression binary: WalkBinary(binary); return;
      case TypeBinaryExpression typeBinary: WalkTypeIs(typeBinary); return;
      case ConditionalExpression conditional: WalkConditional(conditional); return;
      case ConstantExpression constant: WalkConstant(constant); return;
      case ParameterExpression parameter: WalkParameter(parameter); return;
      case MemberExpression member: WalkMember(member); return;
      case MethodCallExpression call: WalkCall(call); return;
      case LambdaExpression lambda: WalkLambda(lambda); return;
      case NewExpression create: WalkNew(create); return;
      case NewArrayExpression newArray: WalkNewArray(newArray); return;
      case InvocationExpression invocation: WalkInvocation(invocation); return;
      case MemberInitExpression memberInit: WalkMemberInit(memberInit); return;
      case ListInitExpression listInit: WalkListInit(listInit); return;
      default:
        throw new NotSupportedException(
          $"A query cannot contain a '{expression.NodeType}' expression ({expression}).");
    }
  }

  protected virtual void WalkUnary(UnaryExpression node) => Walk(node.Operand);

  protected virtual void WalkBinary(BinaryExpression node) {
    Walk(node.Left);
    Walk(node.Right);
  }

  protected virtual void WalkTypeIs(TypeBinaryExpression node) => Walk(node.Expression);

  protected virtual void WalkConditional(ConditionalExpression node) {
    Walk(node.Test);
    Walk(node.IfTrue);
    Walk(node.IfFalse);
  }

  protected virtual void WalkConstant(ConstantExpression node) { }

  protected virtual void WalkParameter(ParameterExpression node) { }

  protected virtual void WalkMember(MemberExpression node) => Walk(node.Expression);

  protected virtual void WalkCall(MethodCallExpression node) {
    Walk(node.Object);
    WalkAll(node.Arguments);
  }

  protected virtual void WalkLambda(LambdaExpression node) {
    foreach (var parameter in node.Parameters)
      Walk(parameter);
    Walk(node.Body);
  }

  protected virtual void WalkNew(NewExpression node) => WalkAll(node.Arguments);

  protected virtual void WalkNewArray(NewArrayExpression node) => WalkAll(node.Expressions);

  protected virtual void WalkInvocation(InvocationExpression node) {
    WalkAll(node.Arguments);
    Walk(node.Expression);
  }

  protected virtual void WalkMemberInit(MemberInitExpression node) {
    WalkNew(node.NewExpression);
    WalkBindings(node.Bindings);
  }

  protected virtual void WalkListInit(ListInitExpression node) {
    WalkNew(node.NewExpression);
    foreach (var initializer in node.Initializers)
      WalkAll(initializer.Arguments);
  }

  protected void WalkAll(ReadOnlyCollection<Expression> expressions) {
    for (var i = 0; i < expressions.Count; i++)
      Walk(expressions[i]);
  }

  protected virtual void WalkBindings(ReadOnlyCollection<MemberBinding> bindings) {
    foreach (var binding in bindings)
      switch (binding) {
        case MemberAssignment assignment: Walk(assignment.Expression); break;
        case MemberMemberBinding nested: WalkBindings(nested.Bindings); break;
        case MemberListBinding list:
          foreach (var initializer in list.Initializers)
            WalkAll(initializer.Arguments);
          break;
        default:
          throw new NotSupportedException($"A query cannot contain a '{binding.BindingType}' member binding.");
      }
  }
}
