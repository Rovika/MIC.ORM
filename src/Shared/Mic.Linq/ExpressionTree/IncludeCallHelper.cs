using System.Linq.Expressions;
using System.Reflection;

namespace Mic.Linq.ExpressionTree;

/// <summary>Provides methods that recognise an <c>Include</c> call in an expression and read its selector.</summary>
internal static class IncludeCallHelper {
  /// <summary>Determines whether a method called in an expression is one of the <c>Include</c> extensions of
  /// <see cref="IncludeQueryableExtensions"/>.</summary>
  /// <param name="method">The method called.</param>
  /// <returns><see langword="true"/> if the method is an <c>Include</c>; otherwise, <see langword="false"/>.</returns>
  public static bool IsInclude(MethodInfo method) {
    ArgumentNullException.ThrowIfNull(method);
    return method.DeclaringType == typeof(IncludeQueryableExtensions)
      && method.Name == nameof(IncludeQueryableExtensions.Include);
  }

  /// <summary>Gets the selector an include call was written with, unwrapping the quote the compiler wraps a lambda
  /// in.</summary>
  /// <param name="call">The include call.</param>
  /// <returns>The selector lambda.</returns>
  public static LambdaExpression GetSelector(MethodCallExpression call) {
    ArgumentNullException.ThrowIfNull(call);
    var argument = call.Arguments[1];
    if (argument is UnaryExpression { NodeType: ExpressionType.Quote } quote)
      argument = quote.Operand;
    if (argument is LambdaExpression selector)
      return selector;
    throw new NotSupportedException($"An Include takes a lambda, not '{argument}'.");
  }
}
