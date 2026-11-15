using System.Collections.Frozen;
using System.Linq.Expressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mic.Common;
using Mic.Linq.ExpressionTree;
using Mic.Linq.Model;
using Mic.Linq.Nodes;
using Scope = Mic.Linq.Translation.NodeEvaluator.Scope;

namespace Mic.Linq.Translation;

/// <summary>Builds a <see cref="NodeEvaluator"/> out of a prepared tree, as a closure per node over its children's,
/// and says which nodes it can build one for.</summary>
/// <remarks>The evaluator's answers match the database's: comparisons and logic are three-valued, a comparison with
/// <see langword="null"/> is an <c>IS NULL</c> test, and the servers' conventions hold wherever they differ from
/// C#'s. Arithmetic is not evaluated, since promotion between numeric types is the vendor's. One builder serves one
/// build.</remarks>
public sealed class NodeEvaluatorBuilder {
  readonly QueryNode? _sqlTierTop;
  readonly List<string> _parameterNames = [];

  // The queryable calls implemented by name alone; Select and SelectMany depend on what was written. GroupBy is not
  // listed: a grouping over cached rows would hand out groups of detached snapshots, so it runs only above a statement.
  static readonly FrozenSet<string> _evaluatedCalls = FrozenSet.ToFrozenSet([
    nameof(Queryable.Where), nameof(Queryable.OrderBy), nameof(Queryable.OrderByDescending),
    nameof(Queryable.ThenBy), nameof(Queryable.ThenByDescending),
    nameof(Queryable.Skip), nameof(Queryable.Take), nameof(Queryable.Distinct),
    nameof(Queryable.First), nameof(Queryable.FirstOrDefault),
    nameof(Queryable.Single), nameof(Queryable.SingleOrDefault),
    nameof(Queryable.Last), nameof(Queryable.LastOrDefault),
    nameof(Queryable.Count), nameof(Queryable.LongCount),
    nameof(Queryable.Any), nameof(Queryable.All), nameof(Queryable.Contains),
    nameof(Queryable.Sum), nameof(Queryable.Min), nameof(Queryable.Max), nameof(Queryable.Average),
  ]);

  // The queryable calls that produce rows rather than one value.
  static readonly FrozenSet<string> _rowSetCalls = FrozenSet.ToFrozenSet([
    nameof(Queryable.Where), nameof(Queryable.OrderBy), nameof(Queryable.OrderByDescending),
    nameof(Queryable.ThenBy), nameof(Queryable.ThenByDescending),
    nameof(Queryable.Skip), nameof(Queryable.Take), nameof(Queryable.Distinct),
    nameof(Queryable.Select), nameof(Queryable.GroupBy), nameof(Queryable.SelectMany),
  ]);

  // The named operations implemented; Coalesce, Conditional, In, IsNull and IsNotNull are handled in BuildValue
  // before Apply is reached.
  static readonly FrozenSet<NamedOpType> _implementedOps = FrozenSet.ToFrozenSet([
    NamedOpType.Upper, NamedOpType.Lower, NamedOpType.Length, NamedOpType.Trim,
    NamedOpType.Substring, NamedOpType.Concat, NamedOpType.Replace, NamedOpType.IndexOf,
    NamedOpType.StartsWith, NamedOpType.EndsWith, NamedOpType.Contains,
    NamedOpType.Year, NamedOpType.Month, NamedOpType.Day, NamedOpType.Date,
    NamedOpType.Abs, NamedOpType.Round, NamedOpType.RoundTo, NamedOpType.Floor, NamedOpType.Ceiling,
    NamedOpType.Modulo, NamedOpType.Power, NamedOpType.Sqrt, NamedOpType.Sign,
    NamedOpType.Coalesce, NamedOpType.In, NamedOpType.JsonValue, NamedOpType.IsNull, NamedOpType.IsNotNull,
    NamedOpType.GeoDistance, NamedOpType.Conditional,
  ]);

  /// <summary>Initializes a new instance of the <see cref="NodeEvaluatorBuilder"/> class.</summary>
  /// <param name="sqlTierTop">For the chain above a statement, the node the SQL was built from, whose rows are the ones
  /// the statement returned; or <see langword="null"/> for a tree the cache answers whole.</param>
  public NodeEvaluatorBuilder(QueryNode? sqlTierTop = null) {
    _sqlTierTop = sqlTierTop;
  }

  /// <summary>Builds the executable for a prepared tree whose every node <see cref="CanEvaluate"/> accepted, or,
  /// where the builder was given a SQL source, for the chain above it.</summary>
  /// <param name="prepared">The root of the prepared tree.</param>
  /// <returns>The executable.</returns>
  /// <remarks>A rowset at the root is listed, so what comes back is the rows and not a lazy sequence.</remarks>
  public NodeEvaluator Build(QueryNode prepared) {
    ArgumentNullException.ThrowIfNull(prepared);
    return new NodeEvaluator(BuildValue(prepared), _parameterNames.ToArray());
  }

  // ----- what can be evaluated ---------------------------------------------------------------------------------------

  /// <summary>Determines whether a node itself, not its subtree, is one the evaluator implements.</summary>
  /// <param name="node">The node.</param>
  /// <returns><see langword="true"/> if the evaluator implements the node; otherwise, <see langword="false"/>.
  /// </returns>
  /// <remarks>A member is read only off a row, and only a value: a cached row is detached, so a reference cannot be
  /// followed and a list cannot be asked. A recursive common table expression is not evaluated.</remarks>
  public static bool CanEvaluate(QueryNode node) => node switch {
    EntitySetNode or LambdaParameterNode or ConstantNode or QueryParameterNode or LambdaNode => true,
    // A new { … } is a row of the transient entity made for it, or with one slot the value itself.
    NewNode => true,
    MemberAccessNode { ModelMember.Kind: LinqMemberKind.Value, Target: LambdaParameterNode } => true,
    // g.Key: what the grouping grouped on, read off the group.
    MemberAccessNode { MemberName: "Key", ModelMember: null, Target: LambdaParameterNode { Entity: GroupShape } } => true,
    BinaryNode binary => binary.IsComparison
      || binary.NodeType is ExpressionType.AndAlso or ExpressionType.OrElse,
    NamedOpNode op => IsImplemented(op.OpType),
    MethodCallNode call when call.IsQueryableCall() => CanRunInCache(call),
    // AsCte says how the SQL is written and nothing about the rows, so in memory it is its source.
    MethodCallNode named when TranslationHelper.IsAsCte(named) => true,
    ExpressionNode { NodeType: ExpressionType.Not or ExpressionType.Convert or ExpressionType.ConvertChecked } => true,
    _ => false,
  };

  /// <summary>Determines whether a queryable call is one the evaluator implements: by its name for most, and for
  /// <c>Select</c> and <c>SelectMany</c> by their argument.</summary>
  /// <param name="call">The call.</param>
  /// <returns><see langword="true"/> if the evaluator implements the call; otherwise, <see langword="false"/>.
  /// </returns>
  public static bool CanRunInCache(MethodCallNode call) {
    ArgumentNullException.ThrowIfNull(call);
    switch (call.MethodName) {
      // Not a bundle: a projection holding whole rows would hold the cache's snapshots, which the host copies into
      // its session only where it finds them as the rows themselves.
      case nameof(Queryable.Select):
        return call.Argument(1) is LambdaNode && call.Entity is not BundleShape;
      // A spread of a JSON array read off each row, held whole in memory; a list member's rows are the server's.
      case nameof(Queryable.SelectMany):
        return call.ArgumentCount is 2 or 3 && call.Argument(1) is LambdaNode { Body.DataType: { } type }
          && JsonTranslationHelper.GetJsonElementType(type) != null && call.Entity is not BundleShape;
      default:
        return _evaluatedCalls.Contains(call.MethodName);
    }
  }

  /// <summary>Gets the one value a selector selects: its body, or the single slot of a one-slot <c>new { … }</c>,
  /// which is how the untyped builder spells a projection of one value.</summary>
  /// <param name="selector">The selector lambda.</param>
  /// <returns>The value, or <see langword="null"/> for a shape of several slots.</returns>
  public static QueryNode? GetSelectedValue(LambdaNode selector) => selector.Body switch {
    NewNode { ChildCount: 1 } one => one.Children[0],
    NewNode => null,
    var body => body,
  };

  /// <summary>Determines whether the evaluator implements a named operation.</summary>
  /// <param name="function">One of the enumeration values that specifies the operation.</param>
  /// <returns><see langword="true"/> if the evaluator implements the operation; otherwise, <see langword="false"/>.
  /// </returns>
  /// <remarks>An operation added to the enumeration without being implemented here sends a query to the server.
  /// </remarks>
  public static bool IsImplemented(NamedOpType function) => _implementedOps.Contains(function);

  // ----- building ------------------------------------------------------------------------------------------------------

  /// <summary>Determines whether a node produces rows: an entity set, or a queryable call over one.</summary>
  private static bool IsRowSet(QueryNode node) => node switch {
    EntitySetNode => true,
    MethodCallNode named when TranslationHelper.IsAsCte(named) => IsRowSet(named.Argument(0)),
    MethodCallNode call when call.IsQueryableCall() => _rowSetCalls.Contains(call.MethodName),
    _ => false,
  };

  /// <summary>Builds the rows a rowset node produces.</summary>
  private Func<Scope, IEnumerable<object?>> BuildRows(QueryNode node) {
    // The node the SQL was built from produces the rows the statement returned; nothing below it is walked.
    if (ReferenceEquals(node, _sqlTierTop))
      return scope => scope.CallContext.ResultRows
        ?? throw new InvalidOperationException("The run has no statement rows for the C# that runs over them.");
    switch (node) {
      case EntitySetNode set:
        return scope => scope.CallContext.GetCachedRows(set.Entity)
          ?? throw new InvalidOperationException($"The run has no cached rows to answer '{set.Entity.Name}' from.");

      // A group's rows are its elements.
      case LambdaParameterNode { Entity: GroupShape } parameter:
        return scope => (IEnumerable<object?>)scope.Get(parameter)!;

      // A named query is its source's rows.
      case MethodCallNode named when TranslationHelper.IsAsCte(named):
        return BuildRows(named.Argument(0));

      case MethodCallNode call: {
        var source = BuildRows(call.Argument(0));
        switch (call.MethodName) {
          // Keys compare as the server compares values.
          case nameof(Queryable.GroupBy): {
            var key = BuildGroupKey(call);
            if (call.ArgumentCount > 2 && call.Argument(2) is LambdaNode { ParameterCount: 1, Body: NewNode })
              throw new NotSupportedException(
                "The rows of a grouping returned as groups are the rows grouped or one value each; a 'new { … }' "
                + "is not built there. Project the groups, as in 'GroupBy(key).Select(g => new { g.Key, … })'.");
            var element = call.ArgumentCount > 2 ? BuildLambda(call, 2) : null;
            return scope => source(scope).GroupBy(
              row => key(scope, row),
              row => element == null ? row : element(scope, row),
              KeyComparer.Instance);
          }
          case nameof(Queryable.Where): {
            var condition = BuildLambda(call, 1);
            return scope => source(scope).Where(row => condition(scope, row) is true);
          }
          // A spread of a JSON array read off each row: its elements, one row each, a null document none.
          case nameof(Queryable.SelectMany): {
            var collection = BuildLambda(call, 1);
            if (call.ArgumentCount == 2)
              return scope => source(scope).SelectMany(row => ReadJsonRows(collection(scope, row)));
            var result = BuildPairLambda(call, 2);
            return scope => source(scope).SelectMany(
              row => ReadJsonRows(collection(scope, row)),
              (row, element) => result(scope, row, element));
          }
          case nameof(Queryable.OrderBy):
          case nameof(Queryable.OrderByDescending): {
            var key = BuildLambda(call, 1);
            var descending = call.MethodName.EndsWith("Descending", StringComparison.Ordinal);
            return scope => descending
              ? source(scope).OrderByDescending(row => key(scope, row), ValueComparer.Instance)
              : source(scope).OrderBy(row => key(scope, row), ValueComparer.Instance);
          }
          case nameof(Queryable.ThenBy):
          case nameof(Queryable.ThenByDescending): {
            var key = BuildLambda(call, 1);
            var descending = call.MethodName.EndsWith("Descending", StringComparison.Ordinal);
            return scope => {
              var ordered = (IOrderedEnumerable<object?>)source(scope);
              return descending
                ? ordered.ThenByDescending(row => key(scope, row), ValueComparer.Instance)
                : ordered.ThenBy(row => key(scope, row), ValueComparer.Instance);
            };
          }
          case nameof(Queryable.Skip): {
            var count = BuildValue(call.Argument(1));
            return scope => source(scope).Skip(Convert.ToInt32(count(scope)));
          }
          case nameof(Queryable.Take): {
            var count = BuildValue(call.Argument(1));
            return scope => source(scope).Take(Convert.ToInt32(count(scope)));
          }
          case nameof(Queryable.Distinct):
            return scope => source(scope).Distinct();
          case nameof(Queryable.Select): {
            var selector = BuildLambda(call, 1, selecting: true);
            return scope => source(scope).Select(row => selector(scope, row));
          }
        }
        break;
      }
    }
    // A JSON array as rows, where an aggregate or a filter is written over a JSON member: its elements.
    if (node.DataType is { } arrayType && JsonTranslationHelper.GetJsonElementType(arrayType) != null) {
      var array = BuildValue(node);
      return scope => ReadJsonRows(array(scope));
    }
    throw new NotSupportedException($"A '{node}' node produces no rows the evaluator can run.");
  }

  /// <summary>Builds the value a node produces; a rowset used where a value is wanted is its rows, listed.</summary>
  private Func<Scope, object?> BuildValue(QueryNode node) {
    if (IsRowSet(node)) {
      var rows = BuildRows(node);
      return scope => rows(scope).ToList();
    }
    switch (node) {
      case LambdaParameterNode parameter:
        return scope => scope.Get(parameter);

      case MemberAccessNode { ModelMember: { } member } access: {
        var target = BuildValue(access.Target!);
        var transient = member.Entity is { IsTransient: true };
        // A row of a transient entity reads itself; anything else is the host's row and the host reads it.
        return scope => target(scope) switch {
          null => null,
          ILinqRow data when transient => data.GetValue(member),
          // An element of a JSON array is read by the member's JSON name, as the server reads a column of the rows.
          var element when transient => ReadJsonValue(element, "$." + member.Name, member.DataType),
          var on => scope.CallContext.ReadMember(on, member),
        };
      }

      // g.Key: what the group was grouped on.
      case MemberAccessNode { MemberName: "Key", ModelMember: null, Target: LambdaParameterNode { Entity: GroupShape } group }:
        return scope => ((IGrouping<object?, object?>)scope.Get(group)!).Key;

      case ConstantNode constant:
        return _ => constant.Value;

      // The getter was given by the layout or by translation, so the evaluator and a statement built from the same
      // tree read the same argument for the same parameter.
      case QueryParameterNode parameter: {
        var getter = parameter.ValueGetter ?? throw new InvalidOperationException(
          $"Parameter '{parameter.Name}' has no getter — its parameters were not laid out before it was prepared.");
        if (parameter.ValueSource == ValueSourceKind.Argument && !_parameterNames.Contains(parameter.Name!))
          _parameterNames.Add(parameter.Name!);
        return scope => getter(scope.CallContext);
      }

      case BinaryNode { NodeType: ExpressionType.AndAlso or ExpressionType.OrElse } logical: {
        var left = BuildValue(logical.Left);
        var right = BuildValue(logical.Right);
        var isAnd = logical.NodeType == ExpressionType.AndAlso;
        // Three-valued: false AND unknown is false, true AND unknown is unknown; reversed for OR.
        return scope => {
          var leftValue = left(scope);
          var rightValue = right(scope);
          if (leftValue is bool leftBool && leftBool != isAnd || rightValue is bool rightBool && rightBool != isAnd)
            return !isAnd;
          return leftValue == null || rightValue == null ? null : isAnd;
        };
      }

      case BinaryNode { NodeType: ExpressionType.Equal or ExpressionType.NotEqual } equality: {
        var wantEqual = equality.NodeType == ExpressionType.Equal;
        var left = BuildValue(equality.Left);
        var right = BuildValue(equality.Right);
        // A parameter that holds null is a test of the other side for null, as the statement writes it.
        var isLeftParameter = equality.Left is QueryParameterNode && equality.Right is not QueryParameterNode;
        var isRightParameter = equality.Right is QueryParameterNode && equality.Left is not QueryParameterNode;
        return scope => {
          var leftValue = left(scope);
          var rightValue = right(scope);
          if (isRightParameter && rightValue == null)
            return leftValue == null == wantEqual;
          if (isLeftParameter && leftValue == null)
            return rightValue == null == wantEqual;
          return leftValue == null || rightValue == null ? null : AreEqual(leftValue, rightValue) == wantEqual;
        };
      }

      case BinaryNode { IsComparison: true } comparison: {
        var left = BuildValue(comparison.Left);
        var right = BuildValue(comparison.Right);
        var type = comparison.NodeType;
        return scope => {
          var leftValue = left(scope);
          var rightValue = right(scope);
          if (leftValue == null || rightValue == null)
            return null;
          var order = CompareValues(leftValue, rightValue);
          return type switch {
            ExpressionType.LessThan => order < 0,
            ExpressionType.LessThanOrEqual => order <= 0,
            ExpressionType.GreaterThan => order > 0,
            _ => order >= 0,
          };
        };
      }

      case ExpressionNode { NodeType: ExpressionType.Not } not: {
        var operand = BuildValue(not.Children[0]);
        // NOT unknown is unknown.
        return scope => operand(scope) is bool value ? !value : null;
      }

      // A conversion, such as an enum to its integer or a value to its nullable, passes the value through.
      case ExpressionNode { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert:
        return BuildValue(convert.Children[0]);

      case NamedOpNode op: {
        var arguments = new Func<Scope, object?>[op.ChildCount];
        for (var i = 0; i < arguments.Length; i++)
          arguments[i] = BuildValue(op.Children[i]);
        if (op.OpType == NamedOpType.In) {
          var (value, list) = (arguments[0], arguments[1]);
          return scope => IsIn(value(scope), list(scope));
        }
        if (op.OpType is NamedOpType.IsNull or NamedOpType.IsNotNull) {
          var tested = arguments[0];
          var wantNull = op.OpType == NamedOpType.IsNull;
          return scope => tested(scope) == null == wantNull;
        }
        // Only the result chosen is computed; a test that is null chooses the second, as a CASE does.
        if (op.OpType == NamedOpType.Conditional) {
          var (test, whenTrue, whenFalse) = (arguments[0], arguments[1], arguments[2]);
          return scope => test(scope) is true ? whenTrue(scope) : whenFalse(scope);
        }
        // Null in, null out for every function but Coalesce.
        return scope => {
          var values = new object?[arguments.Length];
          for (var i = 0; i < values.Length; i++)
            values[i] = arguments[i](scope);
          if (op.OpType == NamedOpType.Coalesce)
            return values.FirstOrDefault(v => v != null);
          if (Array.Exists(values, v => v == null))
            return null;
          // A JSON read is typed by the node, as the server's is by the cast the dialect writes.
          return op.OpType == NamedOpType.JsonValue
            ? ReadJsonValue(values[0]!, values[1]!, op.DataType)
            : Apply(op.OpType, values);
        };
      }

      case MethodCallNode call
          when call.IsQueryableCall():
        return BuildAggregate(call);
    }
    throw new NotSupportedException($"A '{node}' node cannot be evaluated in memory.");
  }

  /// <summary>Builds one value over rows: a count, a test, a membership, a sum, one row.</summary>
  private Func<Scope, object?> BuildAggregate(MethodCallNode call) {
    var count = call.ArgumentCount;
    // Contains takes its collection as a value, a list parameter or a sub-query, and the item as the second argument.
    if (call.MethodName == nameof(Queryable.Contains)) {
      var list = BuildValue(call.Argument(0));
      var item = BuildValue(call.Argument(1));
      return scope => IsIn(item(scope), list(scope));
    }

    var source = BuildRows(call.Argument(0));
    var lambda = count > 1 ? BuildLambda(call, 1) : null;
    // A predicate overload is a filter; a selector overload is what the aggregate is over.
    Func<Scope, IEnumerable<object?>> rows = lambda == null
      ? source
      : call.MethodName is nameof(Queryable.Sum) or nameof(Queryable.Min) or nameof(Queryable.Max) or nameof(Queryable.Average)
        ? scope => source(scope).Select(row => lambda(scope, row))
        : scope => source(scope).Where(row => lambda(scope, row) is true);

    return call.MethodName switch {
      nameof(Queryable.Count) => scope => rows(scope).Count(),
      nameof(Queryable.LongCount) => scope => rows(scope).LongCount(),
      nameof(Queryable.Any) => scope => rows(scope).Any(),
      nameof(Queryable.All) => scope => lambda == null
        ? throw new InvalidOperationException("All needs a predicate.")
        : source(scope).All(row => lambda(scope, row) is true),
      nameof(Queryable.First) => scope => rows(scope).First(),
      nameof(Queryable.FirstOrDefault) => scope => rows(scope).FirstOrDefault(),
      nameof(Queryable.Single) => scope => rows(scope).Single(),
      nameof(Queryable.SingleOrDefault) => scope => rows(scope).SingleOrDefault(),
      nameof(Queryable.Last) => scope => rows(scope).Last(),
      nameof(Queryable.LastOrDefault) => scope => rows(scope).LastOrDefault(),
      // No rows is null for a sum, a minimum, a maximum or an average, as SQL answers.
      nameof(Queryable.Sum) => scope => Fold(rows(scope), call.DataType, values => values.Sum()),
      nameof(Queryable.Min) => scope => Fold(rows(scope), call.DataType, values => values.Min()),
      nameof(Queryable.Max) => scope => Fold(rows(scope), call.DataType, values => values.Max()),
      nameof(Queryable.Average) => scope => Fold(rows(scope), call.DataType, values => values.Average()),
      _ => throw new NotSupportedException($"'{call.MethodName}' cannot be evaluated in memory."),
    };
  }

  /// <summary>Builds a lambda as a function of the row its parameter stands for, with the enclosing scope still
  /// visible.</summary>
  /// <param name="call">The call the lambda is an argument of.</param>
  /// <param name="argument">The zero-based index of the argument.</param>
  /// <param name="selecting"><see langword="true"/> if the lambda selects, so that its body is one value or a
  /// <c>new { … }</c> that is a row of the call's transient entity; otherwise, <see langword="false"/>.</param>
  private Func<Scope, object?, object?> BuildLambda(MethodCallNode call, int argument, bool selecting = false) {
    var lambda = GetLambda(call, argument, parameters: 1);
    var parameter = (LambdaParameterNode)lambda.Children[0];
    var body = selecting ? BuildSelected(lambda, call) : BuildValue(lambda.Body);
    return (scope, row) => body(scope.Bind(parameter, row));
  }

  /// <summary>Builds a result selector over a pair, the row and the element spread off it, as a function of both.
  /// </summary>
  private Func<Scope, object?, object?, object?> BuildPairLambda(MethodCallNode call, int argument) {
    var lambda = GetLambda(call, argument, parameters: 2);
    var first = (LambdaParameterNode)lambda.Children[0];
    var second = (LambdaParameterNode)lambda.Children[1];
    var body = BuildSelected(lambda, call);
    return (scope, row, element) => body(scope.Bind(first, row).Bind(second, element));
  }

  private static LambdaNode GetLambda(MethodCallNode call, int argument, int parameters) {
    var lambda = call.Argument(argument) as LambdaNode
      ?? throw new NotSupportedException($"Argument {argument} of {call.MethodName} is not a lambda.");
    if (lambda.ParameterCount != parameters)
      throw new NotSupportedException(
        $"A {lambda.ParameterCount}-parameter lambda is written where {call.MethodName} takes one of {parameters}.");
    return lambda;
  }

  /// <summary>Builds what a selector selects over its bound scope: its one value, or for a <c>new { … }</c> of
  /// several slots a row of the transient entity made for the call, holding each slot under its name.</summary>
  private Func<Scope, object?> BuildSelected(LambdaNode selector, MethodCallNode call) {
    if (GetSelectedValue(selector) is { } value)
      return BuildValue(value);
    var composite = (NewNode)selector.Body;
    var shape = TranslationHelper.GetRowEntity(call.Entity
      ?? throw new InvalidOperationException($"The projection in {call.MethodName} was not bound to an entity."));
    if (!shape.IsTransient || shape.Members.Count != composite.ChildCount)
      throw new NotSupportedException(
        $"The projection in {call.MethodName} makes rows of '{shape.Name}', which the evaluator cannot fill slot by slot.");
    var parts = new Func<Scope, object?>[composite.ChildCount];
    for (var i = 0; i < parts.Length; i++)
      parts[i] = BuildValue(composite.Children[i]);
    return scope => {
      var row = scope.CallContext.CreateRow(shape);
      for (var i = 0; i < parts.Length; i++)
        row.SetValue(shape.Members[i], parts[i](scope));
      return row;
    };
  }

  /// <summary>Builds a grouping's key as a function of the row: the selector's one value, or for a composite a row of
  /// the group shape holding each part under its name.</summary>
  private Func<Scope, object?, object?> BuildGroupKey(MethodCallNode call) => BuildLambda(call, 1, selecting: true);

  /// <summary>Folds the non-null values as decimals and converts the result to what the call asked for.</summary>
  private static object? Fold(IEnumerable<object?> rows, Type? asked, Func<IEnumerable<decimal>, decimal> fold) {
    var values = rows.Where(v => v != null).Select(v => ToDecimal(v!)).ToList();
    if (values.Count == 0)
      return null;
    var result = fold(values);
    var target = asked == null ? null : Nullable.GetUnderlyingType(asked) ?? asked;
    return target == null || target == typeof(decimal) ? result : Convert.ChangeType(result, target);
  }

  private static object? IsIn(object? wanted, object? list) {
    if (wanted == null || list is not System.Collections.IEnumerable items)
      return null;
    foreach (var item in items)
      if (item != null && AreEqual(wanted, item))
        return true;
    return false;
  }

  // ----- what a value means in memory ------------------------------------------------------------------------------
  //
  // The functions, the equality and the ordering agree with the servers' conventions where they differ from C#'s.
  // Nulls are the closures' above: a function here is called with values that are all there.

  /// <summary>Reads the value at a path in a document held in memory, as an object, JSON text or a node, as the given
  /// type or else as the plain value the JSON holds; <see langword="null"/> where the path names nothing.</summary>
  private static object? ReadJsonValue(object document, object path, Type? asType) {
    var value = ReadJsonValue(document, AsText(path));
    if (value == null || asType == null)
      return value;
    var wanted = Nullable.GetUnderlyingType(asType) ?? asType;
    if (wanted.IsInstanceOfType(value) || wanted == typeof(object))
      return value;
    var invariant = System.Globalization.CultureInfo.InvariantCulture;
    if (wanted == typeof(DateOnly))
      return DateOnly.Parse(AsText(value), invariant);
    if (wanted == typeof(DateTime))
      return DateTime.Parse(AsText(value), invariant);
    if (wanted == typeof(Guid))
      return Guid.Parse(AsText(value));
    if (wanted.IsEnum)
      return value is string name ? Enum.Parse(wanted, name, ignoreCase: true) : Enum.ToObject(wanted, value);
    return wanted.IsPrimitive || wanted == typeof(decimal) || wanted == typeof(string)
      ? Convert.ChangeType(value, wanted, invariant)
      : value;
  }

  private static object? ReadJsonValue(object document, string path) {
    var node = document switch {
      JsonNode held => held,
      string text => JsonNode.Parse(text),
      _ => JsonSerializer.SerializeToNode(document, document.GetType()),
    };
    // Each step of $.a.b[0] is a property, or an index in brackets.
    foreach (var step in path.TrimStart('$').Split('.', StringSplitOptions.RemoveEmptyEntries)) {
      if (node == null)
        return null;
      var name = step;
      var index = -1;
      var bracket = step.IndexOf('[');
      if (bracket >= 0 && step.EndsWith(']')) {
        name = step[..bracket];
        index = int.Parse(step[(bracket + 1)..^1], System.Globalization.CultureInfo.InvariantCulture);
      }
      if (name.Length > 0)
        node = node is JsonObject owner && owner.TryGetPropertyValue(name, out var value) ? value : null;
      if (index >= 0 && node != null)
        node = node is JsonArray items && index < items.Count ? items[index] : null;
    }
    return GetPlainValue(node);
  }

  /// <summary>Gets a node as the plain value it holds, or an object or an array as its JSON text.</summary>
  private static object? GetPlainValue(JsonNode? node) => node switch {
    null => null,
    JsonValue value => value.GetValueKind() switch {
      JsonValueKind.String => value.GetValue<string>(),
      JsonValueKind.Number => value.TryGetValue<long>(out var whole) ? whole : value.GetValue<double>(),
      JsonValueKind.True => true,
      JsonValueKind.False => false,
      _ => null,
    },
    _ => node.ToJsonString(),
  };

  /// <summary>Reads the elements of a JSON array held in memory as a list, JSON text or a node, one row each: a scalar
  /// element as its plain value, an object element as its node; nothing for <see langword="null"/>.</summary>
  private static IEnumerable<object?> ReadJsonRows(object? array) => array switch {
    null => [],
    string text => ReadJsonRows(JsonNode.Parse(text)),
    JsonArray items => items.Select(item => item is JsonValue ? GetPlainValue(item) : item),
    JsonNode => [],
    System.Collections.IEnumerable list => list.Cast<object?>(),
    _ => [],
  };

  /// <summary>Applies a named operation to values in memory, none of them <see langword="null"/>.</summary>
  private static object? Apply(NamedOpType function, object?[] values) {
    switch (function) {
      case NamedOpType.JsonValue: return ReadJsonValue(values[0]!, values[1]!, null);
      case NamedOpType.GeoDistance: return GeoFunctions.Distance((GeoPoint)values[0]!, (GeoPoint)values[1]!);
      case NamedOpType.Upper: return AsText(values[0]).ToUpperInvariant();
      case NamedOpType.Lower: return AsText(values[0]).ToLowerInvariant();
      case NamedOpType.Length: return AsText(values[0]).Length;
      case NamedOpType.Trim: return AsText(values[0]).Trim();
      case NamedOpType.Concat: return AsText(values[0]) + AsText(values[1]);
      case NamedOpType.Replace: return AsText(values[0]).Replace(AsText(values[1]), AsText(values[2]));
      // The server's convention, 1-based and 0 for absent; the converter subtracts the one outside this node.
      case NamedOpType.IndexOf:
        return AsText(values[0]).IndexOf(AsText(values[1]), StringComparison.Ordinal) + 1;

      case NamedOpType.Substring: {
        var text = AsText(values[0]);
        var start = Math.Max(0, Convert.ToInt32(values[1]) - 1);
        var length = values.Length > 2 ? Convert.ToInt32(values[2]) : text.Length - start;
        if (start >= text.Length || length <= 0)
          return "";
        return text.Substring(start, Math.Min(length, text.Length - start));
      }

      // The tree carries the raw term, so a match is a plain string operation with nothing to escape.
      case NamedOpType.StartsWith: return AsText(values[0]).StartsWith(AsText(values[1]), StringComparison.Ordinal);
      case NamedOpType.EndsWith: return AsText(values[0]).EndsWith(AsText(values[1]), StringComparison.Ordinal);
      case NamedOpType.Contains: return AsText(values[0]).Contains(AsText(values[1]), StringComparison.Ordinal);

      case NamedOpType.Year: return AsDate(values[0]).Year;
      case NamedOpType.Month: return AsDate(values[0]).Month;
      case NamedOpType.Day: return AsDate(values[0]).Day;
      case NamedOpType.Date: return DateOnly.FromDateTime(AsDate(values[0]));

      case NamedOpType.Abs: return Math.Abs(ToDecimal(values[0]!));
      case NamedOpType.Round: return Math.Round(ToDecimal(values[0]!), MidpointRounding.AwayFromZero);
      case NamedOpType.RoundTo:
        return Math.Round(ToDecimal(values[0]!), Convert.ToInt32(values[1]), MidpointRounding.AwayFromZero);
      case NamedOpType.Floor: return Math.Floor(ToDecimal(values[0]!));
      case NamedOpType.Ceiling: return Math.Ceiling(ToDecimal(values[0]!));
      case NamedOpType.Modulo: return ToDecimal(values[0]!) % ToDecimal(values[1]!);
      case NamedOpType.Power:
        return (decimal)Math.Pow((double)ToDecimal(values[0]!), (double)ToDecimal(values[1]!));
      case NamedOpType.Sqrt: return (decimal)Math.Sqrt((double)ToDecimal(values[0]!));
      case NamedOpType.Sign: return Math.Sign(ToDecimal(values[0]!));

      default:
        throw new NotSupportedException($"'{function}' cannot be evaluated in memory yet.");
    }
  }

  private static string AsText(object? value) => value as string ?? value?.ToString() ?? "";

  private static DateTime AsDate(object? value) => value switch {
    DateTime at => at,
    DateOnly on => on.ToDateTime(TimeOnly.MinValue),
    _ => Convert.ToDateTime(value),
  };

  /// <summary>Determines equality as the database would: by <c>Equals</c> for values of one type, and as numbers
  /// where two numeric types differ.</summary>
  private static bool AreEqual(object leftValue, object rightValue) =>
    leftValue.GetType() == rightValue.GetType() ? leftValue.Equals(rightValue)
    : IsNumeric(leftValue) && IsNumeric(rightValue) ? ToDecimal(leftValue) == ToDecimal(rightValue)
    : leftValue.Equals(rightValue);

  private static int CompareValues(object leftValue, object rightValue) {
    if (leftValue.GetType() == rightValue.GetType() && leftValue is IComparable same)
      return same.CompareTo(rightValue);
    if (IsNumeric(leftValue) && IsNumeric(rightValue))
      return ToDecimal(leftValue).CompareTo(ToDecimal(rightValue));
    return leftValue is IComparable comparable ? comparable.CompareTo(rightValue)
      : throw new NotSupportedException($"A {leftValue.GetType().Name} and a {rightValue.GetType().Name} cannot be ordered.");
  }

  private static bool IsNumeric(object value) => value is byte or sbyte or short or ushort or int or uint or long or ulong
    or decimal or double or float;

  private static decimal ToDecimal(object value) =>
    Convert.ToDecimal(value, System.Globalization.CultureInfo.InvariantCulture);

  /// <summary>Compares group keys as a server does: values by the evaluator's equality, a composite part by part,
  /// nulls equal to each other.</summary>
  /// <remarks>A number hashes as its decimal, so that the hash agrees with the equality.</remarks>
  private sealed class KeyComparer : IEqualityComparer<object?> {
    public static readonly KeyComparer Instance = new();

    public new bool Equals(object? leftValue, object? rightValue) {
      if (leftValue == null || rightValue == null)
        return leftValue == null && rightValue == null;
      if (leftValue is ILinqRow leftRow && rightValue is ILinqRow rightRow) {
        var members = leftRow.Entity.Members;
        for (var i = 0; i < members.Count; i++)
          if (!Equals(leftRow.GetValue(members[i]), rightRow.GetValue(members[i])))
            return false;
        return true;
      }
      return AreEqual(leftValue, rightValue);
    }

    public int GetHashCode(object? value) {
      switch (value) {
        case null:
          return 0;
        case ILinqRow row: {
          var hash = 17;
          foreach (var member in row.Entity.Members)
            hash = hash * 31 + GetHashCode(row.GetValue(member));
          return hash;
        }
        case string or bool or Guid or Enum:
          return value.GetHashCode();
        case IConvertible:
          return ToDecimal(value).GetHashCode();
        default:
          return value.GetHashCode();
      }
    }
  }

  /// <summary>Orders values as a server does: nulls first, values by the evaluator's comparison. This class cannot be
  /// inherited.</summary>
  private sealed class ValueComparer : IComparer<object?> {
    public static readonly ValueComparer Instance = new();
    public int Compare(object? leftValue, object? rightValue) =>
      leftValue == null ? (rightValue == null ? 0 : -1) : rightValue == null ? 1 : CompareValues(leftValue, rightValue);
  }
}
