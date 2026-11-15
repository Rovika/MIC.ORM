using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Mic.Linq.ExpressionTree;

/// <summary>Rewrites an expression tree to read its captured locals from copies made at the call, so that a later
/// assignment to a local does not reach the tree.</summary>
/// <remarks>The compiler keeps the locals a lambda captures in the fields of a closure object, which the tree holds as
/// a constant. Each closure object is copied once, a closure it refers to is copied with it, and the tree's constants
/// are pointed at the copies; the tree keeps its shape. The locals are copied, not what they refer to: a captured
/// entity is the same entity, and a captured query is rebuilt over the copies of its own closures.</remarks>
public sealed class CapturedLocalSnapshotter : ExpressionVisitor {
  static readonly MethodInfo CloneMethod =
    typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!;

  readonly Dictionary<object, object> _copies = new(ReferenceEqualityComparer.Instance);

  private CapturedLocalSnapshotter() { }

  /// <summary>Returns the expression with every closure object it holds replaced by a copy.</summary>
  /// <param name="expression">The expression to rewrite.</param>
  /// <returns>The rewritten expression, or <paramref name="expression"/> itself if it captures no local.</returns>
  public static Expression Snapshot(Expression expression) {
    ArgumentNullException.ThrowIfNull(expression);
    return new CapturedLocalSnapshotter().Visit(expression);
  }

  /// <summary>Replaces a constant that is a closure object by a constant of its copy.</summary>
  /// <param name="node">The constant.</param>
  /// <returns>The constant of the copy, or <paramref name="node"/> for any other constant.</returns>
  protected override Expression VisitConstant(ConstantExpression node) =>
    node.Value is { } value && IsClosure(value.GetType()) ? Expression.Constant(CopyClosure(value), node.Type) : node;

  /// <summary>Returns the copy of a closure object, made on the first call for it.</summary>
  private object CopyClosure(object closure) {
    if (_copies.TryGetValue(closure, out var copy))
      return copy;
    copy = CloneMethod.Invoke(closure, null)!;
    // Registered before its fields are walked: a query held in a field may read this closure again.
    _copies[closure] = copy;
    const BindingFlags instanceFields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    foreach (var field in copy.GetType().GetFields(instanceFields)) {
      switch (field.GetValue(copy)) {
        case { } inner when IsClosure(inner.GetType()):
          field.SetValue(copy, CopyClosure(inner));
          break;
        case IQueryable query when RebuildQuery(query) is { } rebuilt && field.FieldType.IsInstanceOfType(rebuilt):
          field.SetValue(copy, rebuilt);
          break;
      }
    }
    return copy;
  }

  /// <summary>Returns the query over the copies of the closures its expression holds, or <see langword="null"/> if
  /// it holds none.</summary>
  private IQueryable? RebuildQuery(IQueryable query) {
    var rewritten = Visit(query.Expression);
    return ReferenceEquals(rewritten, query.Expression) ? null : query.Provider.CreateQuery(rewritten);
  }

  /// <summary>Determines whether a type is a closure class the C# compiler made for captured locals.</summary>
  private static bool IsClosure(Type type) =>
    type.Name.Contains("DisplayClass", StringComparison.Ordinal)
    && type.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false);
}
