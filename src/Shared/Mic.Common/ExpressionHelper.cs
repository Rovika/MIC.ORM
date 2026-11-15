using System.Linq.Expressions;
using System.Reflection;

namespace Mic.Common;

/// <summary>Provides functionality to read a value out of an expression tree without compiling it: the captured
/// object the value is read from, a getter composed once per expression shape, and a one-off evaluation.</summary>
/// <remarks>A value is a subtree over a captured variable, such as the compiler's display class field a lambda's
/// <c>minPrice</c> became; which subtrees are values is the caller's question. Nothing is compiled: a getter is a
/// closure over reflection, which runs the same on WebAssembly, where there is no JIT.</remarks>
public static class ExpressionHelper {

  /// <summary>Finds the captured object, such as a compiler display class, that an expression reads from.</summary>
  /// <param name="expression">The expression.</param>
  /// <param name="hasSource">When this method returns, contains <see langword="true"/> if the expression reads from a
  /// captured object; otherwise, <see langword="false"/>.</param>
  /// <returns>The captured object, or <see langword="null"/> when the expression reads from nothing, such as a static
  /// member or an expression over literals.</returns>
  /// <remarks><see langword="null"/> is a valid answer, not a failure: <c>DateTime.Today</c> reads from nothing and is
  /// still read afresh on every call. An expression that reads from more than one captured object is not supported.
  /// </remarks>
  public static object? FindSource(Expression expression, out bool hasSource) {
    var sources = new List<object?>();
    Collect(expression, sources);
    hasSource = sources.Count > 0;
    if (sources.Count > 1)
      throw new NotSupportedException(
        $"A value reading from more than one captured object is not supported yet ({expression}).");
    return sources.Count == 0 ? null : sources[0];
  }

  private static void Collect(Expression expression, List<object?> sources) {
    switch (expression) {
      case ConstantExpression:
        // A literal standing alone is not a source; only a constant reached as a member's target is.
        return;
      case MemberExpression { Expression: ConstantExpression target }:
        if (!sources.Any(f => ReferenceEquals(f, target.Value)))
          sources.Add(target.Value);
        return;
      case MemberExpression member:
        if (member.Expression != null)
          Collect(member.Expression, sources);
        return;
      case UnaryExpression unary:
        Collect(unary.Operand, sources);
        return;
      case MethodCallExpression call:
        if (call.Object != null)
          Collect(call.Object, sources);
        foreach (var argument in call.Arguments)
          Collect(argument, sources);
        return;
      case NewArrayExpression array:
        foreach (var item in array.Expressions)
          Collect(item, sources);
        return;
      case BinaryExpression binary:
        Collect(binary.Left, sources);
        Collect(binary.Right, sources);
        return;
      default:
        throw MakeUnsupported(expression);
    }
  }

  /// <summary>Composes a getter that, given the captured object, produces the expression's value.</summary>
  /// <param name="expression">The expression to read.</param>
  /// <returns>A delegate that takes the object <see cref="FindSource"/> returned, ignored when there was none, and
  /// returns the value.</returns>
  /// <remarks>The reflection cost is paid once, when the getter is composed; a call of the getter is a chain of
  /// delegate calls, so a getter is meant to be held and run again for each call's own captured object.</remarks>
  public static Func<object?, object?> Compose(Expression expression) {
    switch (expression) {
      case ConstantExpression constant:
        return _ => constant.Value;

      // Reading a field of the display class: the argument stands in for the constant, so each call reads its own
      // object.
      case MemberExpression { Expression: ConstantExpression } member: {
        // Keep the member, not the expression: the expression holds the first caller's display class.
        var read = member.Member;
        return source => ReadMember(read, source);
      }

      case MemberExpression { Expression: null } staticMember: {
        var read = staticMember.Member;
        return _ => ReadMember(read, null);
      }

      case MemberExpression member: {
        var inner = Compose(member.Expression!);
        var read = member.Member;
        return source => ReadMember(read, inner(source));
      }

      case UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert: {
        var inner = Compose(convert.Operand);
        var type = convert.Type;
        return source => ConvertTo(inner(source), type);
      }

      case MethodCallExpression call: {
        // A ref struct never boxes, so neither a getter nor a bound parameter could carry one.
        if (call.Method.ReturnType.IsByRefLike)
          throw new NotSupportedException(
            $"A value cannot be a '{call.Method.ReturnType.Name}' — it is a ref struct and cannot be bound.");
        var target = call.Object == null ? null : Compose(call.Object);
        var arguments = call.Arguments.Select(Compose).ToArray();
        var method = call.Method;
        return source => {
          var values = new object?[arguments.Length];
          for (var i = 0; i < values.Length; i++)
            values[i] = arguments[i](source);
          return method.Invoke(target?.Invoke(source), values);
        };
      }

      // new[] { a, b, c }: how a collection reaches an IN.
      case NewArrayExpression { NodeType: ExpressionType.NewArrayInit } array: {
        var items = array.Expressions.Select(Compose).ToArray();
        var elementType = array.Type.GetElementType()!;
        return source => {
          var values = Array.CreateInstance(elementType, items.Length);
          for (var i = 0; i < items.Length; i++)
            values.SetValue(items[i](source), i);
          return values;
        };
      }

      // An index into an array is a node of its own; on a list it is a get_Item call, which the case above handles.
      case BinaryExpression { NodeType: ExpressionType.ArrayIndex } index: {
        var array = Compose(index.Left);
        var at = Compose(index.Right);
        return source => (array(source) as Array)?.GetValue(Convert.ToInt32(at(source)));
      }

      // Arithmetic over values the caller holds, such as minPrice * 1.1m, is computed once per call, so a query binds
      // a single value.
      case BinaryExpression { NodeType: ExpressionType.Add or ExpressionType.Subtract or ExpressionType.Multiply
        or ExpressionType.Divide } arithmetic: {
        var left = Compose(arithmetic.Left);
        var right = Compose(arithmetic.Right);
        var kind = arithmetic.NodeType;
        var type = arithmetic.Type;
        return source => {
          var leftValue = left(source);
          var rightValue = right(source);
          if (leftValue == null || rightValue == null)
            return null;
          if (kind == ExpressionType.Add && (leftValue is string || rightValue is string))
            return leftValue.ToString() + rightValue;
          var value = kind switch {
            ExpressionType.Add => Convert.ToDecimal(leftValue) + Convert.ToDecimal(rightValue),
            ExpressionType.Subtract => Convert.ToDecimal(leftValue) - Convert.ToDecimal(rightValue),
            ExpressionType.Multiply => Convert.ToDecimal(leftValue) * Convert.ToDecimal(rightValue),
            _ => Convert.ToDecimal(leftValue) / Convert.ToDecimal(rightValue),
          };
          return ConvertTo(value, type);
        };
      }

      default:
        throw MakeUnsupported(expression);
    }
  }

  /// <summary>Evaluates an expression once and returns its value.</summary>
  /// <param name="expression">The expression to evaluate.</param>
  /// <returns>The value of the expression.</returns>
  /// <remarks>For a value that is read repeatedly, compose a getter with <see cref="Compose"/> and hold it instead.
  /// </remarks>
  public static object? Evaluate(Expression expression) {
    var source = FindSource(expression, out _);
    return Compose(expression)(source);
  }

  private static object? ReadMember(MemberInfo member, object? target) => member switch {
    FieldInfo field => field.GetValue(target),
    PropertyInfo property => property.GetValue(target),
    _ => throw new NotSupportedException($"A value cannot be read from a {member.MemberType} ({member.Name})."),
  };

  /// <summary>Converts a value to the target type of a <c>Convert</c> node.</summary>
  private static object? ConvertTo(object? value, Type target) {
    if (value == null)
      return null;
    var wanted = Nullable.GetUnderlyingType(target) ?? target;
    if (wanted.IsInstanceOfType(value))
      return value;
    if (wanted.IsEnum)
      return Enum.ToObject(wanted, value);
    return Convert.ChangeType(value, wanted);
  }

  private static NotSupportedException MakeUnsupported(Expression expression) =>
    new($"A value of the form '{expression}' is not supported yet — it is a '{expression.NodeType}' expression, "
      + "and the values that can be read are a captured variable, a chain through one, a static member, a call on "
      + "either, an index into one, arithmetic over them, and an array of those.");

}
