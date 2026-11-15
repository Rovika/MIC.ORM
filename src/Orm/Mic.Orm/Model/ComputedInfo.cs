using System.Linq.Expressions;
using System.Reflection;

namespace Mic.Orm.Model;

/// <summary>Represents how a member declared with <c>[Computed]</c> gets its value: a static method over the entity,
/// called on every read. This class cannot be inherited.</summary>
public sealed class ComputedInfo {
  /// <summary>Gets the static method that computes the value, taking the entity and returning the member's type.
  /// </summary>
  public MethodInfo Method { get; }

  /// <summary>Gets a value that indicates whether the value is written into a column at a save.</summary>
  public bool Stored { get; }

  /// <summary>Gets the method compiled as a call over the entity instance.</summary>
  public Func<object, object?> Compute { get; }

  public ComputedInfo(MethodInfo method, bool stored) {
    ArgumentNullException.ThrowIfNull(method);
    Method = method;
    Stored = stored;
    Compute = BuildCompute(method);
  }

  private static Func<object, object?> BuildCompute(MethodInfo method) {
    var entity = Expression.Parameter(typeof(object), "entity");
    var call = Expression.Call(method, Expression.Convert(entity, method.GetParameters()[0].ParameterType));
    return Expression.Lambda<Func<object, object?>>(Expression.Convert(call, typeof(object)), entity).Compile();
  }
}
