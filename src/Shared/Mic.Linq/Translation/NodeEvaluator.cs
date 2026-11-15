using Mic.Linq.Nodes;

namespace Mic.Linq.Translation;

/// <summary>Represents a prepared tree as an executable run over rows in memory: a query the cache answers whole, or
/// the chain above the node a statement was built from, run over the rows the statement returned. This class cannot
/// be inherited.</summary>
/// <remarks>The evaluator is built once per shape by <see cref="NodeEvaluatorBuilder"/> as a closure per node, since
/// compiled lambdas are not available on the client, and is run per call.</remarks>
public sealed class NodeEvaluator {
  /// <summary>Gets the names of the parameters the query reads an argument for, in the order it first names them.
  /// </summary>
  /// <remarks>A parameter with a getter of its own is not listed. Which argument each parameter reads is its slot,
  /// not its place in this list.</remarks>
  public IReadOnlyList<string> ParameterNames { get; }

  readonly Func<Scope, object?> _evaluate;

  internal NodeEvaluator(Func<Scope, object?> evaluate, IReadOnlyList<string> parameterNames) {
    _evaluate = evaluate;
    ParameterNames = parameterNames;
  }

  /// <summary>Runs the query.</summary>
  /// <param name="context">The context of the call.</param>
  /// <returns>Whatever the root produces: rows of a cached entity, a list of values, or one value.</returns>
  public object? Evaluate(ILinqCallContext context) {
    ArgumentNullException.ThrowIfNull(context);
    return _evaluate(new Scope(context));
  }

  /// <summary>Represents one run: the call's context, and the rows bound to lambda parameters, innermost last, which
  /// a nested lambda sees through.</summary>
  internal sealed class Scope {
    /// <summary>Gets the context of the call the run answers.</summary>
    public ILinqCallContext CallContext { get; }

    readonly Scope? _outer;
    readonly LambdaParameterNode? _parameter;
    readonly object? _row;

    public Scope(ILinqCallContext callContext) {
      CallContext = callContext;
    }

    private Scope(Scope outer, LambdaParameterNode parameter, object? row) {
      CallContext = outer.CallContext;
      _outer = outer;
      _parameter = parameter;
      _row = row;
    }

    public Scope Bind(LambdaParameterNode parameter, object? row) => new(this, parameter, row);

    public object? Get(LambdaParameterNode parameter) {
      for (var scope = this; scope != null; scope = scope._outer)
        if (ReferenceEquals(scope._parameter, parameter))
          return scope._row;
      throw new InvalidOperationException($"Lambda parameter '{parameter}' is not bound to a row.");
    }
  }
}
