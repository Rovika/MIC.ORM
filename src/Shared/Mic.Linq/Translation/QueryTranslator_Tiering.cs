using System.Linq.Expressions;
using Mic.Common;
using Mic.Linq.ExpressionTree;
using Mic.Linq.Model;
using Mic.Linq.Nodes;

namespace Mic.Linq.Translation;

// ----- phase 1: the PreSql tier, for values the caller brought and for queries over cached entities ------------------

public sealed partial class QueryTranslator {

  /// <summary>Marks the PreSql subtrees and replaces the top of each with a parameter whose getter reads the value out
  /// of the run; the root itself is replaced when the whole query is one value the caller brought.</summary>
  /// <remarks>A subtree is PreSql when nothing inside it names a row and nothing inside it is a query. A bare constant
  /// is not replaced, since a constant is shape; neither is structure such as a lambda or a projection. Only a node
  /// converted from C# is replaced, since only such a node has an expression to read a value through.</remarks>
  private void AssignPreSql() {
    MarkPreSql(_root);
    _root = _root.Transform(node => IsReplaceable(node) ? MakeValueParameter((ExpressionNode)node) : node);
  }

  /// <summary>Determines whether nothing in a subtree names a row or is a query, marking the tier on the way up.
  /// </summary>
  private static bool MarkPreSql(QueryNode node) {
    var preSql = node switch {
      // A call that returns a query is a query however it is spelled.
      LambdaParameterNode or RowSetNode or RowNode => false,
      MethodCallNode { ReturnType: { } returns } when typeof(IQueryable).IsAssignableFrom(returns) => false,
      _ => true,
    };
    // Every child is asked whatever the answer so far: a subtree under a row may still hold values the caller
    // brought.
    foreach (var child in node.Children)
      preSql &= MarkPreSql(child);
    if (preSql && node is not (ConstantNode or QueryParameterNode))
      node.Tier = QueryTier.PreSql;
    return preSql;
  }

  /// <summary>Determines whether a PreSql node is worth replacing: converted from C#, a value rather than structure,
  /// and not a literal.</summary>
  private static bool IsReplaceable(QueryNode node) =>
    node is ExpressionNode { Tier: QueryTier.PreSql, Source: not null, NodeType: not ExpressionType.Constant }
    && node is not (LambdaNode or NewNode);

  /// <summary>Makes the parameter that stands for a value: named after the read it makes, typed as the expression is,
  /// reading through the closure the scan found, or through nothing for a static read.</summary>
  /// <remarks>A read with no object to read from, such as <c>DateTime.Today</c>, is still a parameter and not a
  /// constant, so that its value is not fixed at translation.</remarks>
  private QueryParameterNode MakeValueParameter(ExpressionNode node) {
    var expression = UnwrapConversions(node.Source!);
    var source = ExpressionHelper.FindSource(expression, out var hasSource);
    var getter = ExpressionHelper.Compose(expression);
    var name = BuildParameterName(expression);
    QueryParameterNode parameter;
    if (!hasSource) {
      parameter = QueryMaker.Parameter(name, expression.Type, _ => getter(null));
    } else {
      if (!_info.IsTyped)
        throw new InvalidOperationException(
          $"'{expression}' reads a captured object, and a query built by name has no scan to find it in — write a "
          + "Parameter(…) instead.");
      var index = _info.IndexOf(source);
      if (index < 0)
        throw new InvalidOperationException(
          $"The scan of this query did not record the object '{expression}' reads from. The scanner assigns argument "
          + "positions and the tiering pass reads them; they have gone out of step.");
      // The getter reaches for its own argument; the index is kept beside it for whoever counts argument slots.
      parameter = QueryMaker.Parameter(name, expression.Type, callCtx => getter(callCtx.GetArgument(index)));
      parameter.ValueSource = ValueSourceKind.LocalVar;
      parameter.ArgIndex = index;
    }
    parameter.Tier = QueryTier.PreSql;
    return parameter;
  }

  // ----- the cache mark, for queries over cached entities -------------------------------------------------------------

  /// <summary>Marks what runs in memory over cached entities: a cached entity set, and everything above it the
  /// evaluator can do.</summary>
  /// <returns><see langword="true"/> if the whole query runs in memory; otherwise, <see langword="false"/>.</returns>
  /// <remarks>A node is marked <see cref="TranslationFlags.Cached"/> when the evaluator implements it and every child
  /// is marked or PreSql. Where an unmarked node has a marked child, the child becomes a parameter whose getter
  /// evaluates it out of the run if it is a value, or its whole chain loses the mark if it is rows of an entity, for
  /// which there is no parameter. The tiers are untouched: the statement is built for a query the cache answers too,
  /// for a caller that skips the cache. The cached entities read in memory are kept for the passes after.</remarks>
  private bool MarkRunnableInCache() {
    _cachedParameterCount = 0;
    var whole = MarkRunnableInCache(_root);
    if (!whole)
      ResolveTierBoundaries(_root);
    // Collected after the boundaries are resolved: the sets a chain sent back to the server reads are not in memory.
    _translatedQuery.CachedEntities = CollectCachedEntities();
    return whole;
  }

  private bool MarkRunnableInCache(QueryNode node) {
    bool inMemory;
    switch (node) {
      case EntitySetNode set:
        // The cached blocks were filled under the filters, so a query without them reads nothing from the cache;
        // neither does a query that skips the cache or locks its rows.
        inMemory = set.Entity.IsCached && !_translatedQuery.IgnoresFilters && !_readsServerOnly;
        break;
      // The rows of a parameter, and a function's rows, are the server's to read.
      case JsonRowsNode or TableFunctionNode:
        inMemory = false;
        break;
      // A parameter stands for a row of its source, marked before the lambda written over it is walked.
      case LambdaParameterNode parameter:
        inMemory = parameter.RowSource?.TranslationFlags.HasFlag(TranslationFlags.Cached) == true;
        break;
      case ConstantNode:
      case QueryParameterNode:
        inMemory = true;
        break;
      case MethodCallNode call
          when call.IsQueryableCall(): {
        // The source first, so the lambdas written over it find their rows' source marked.
        inMemory = MarkRunnableInCache(call.Argument(0));
        for (var i = 1; i < call.ArgumentCount; i++)
          inMemory &= MarkRunnableInCache(call.Argument(i));
        inMemory &= NodeEvaluatorBuilder.CanEvaluate(node);
        break;
      }
      default:
        inMemory = NodeEvaluatorBuilder.CanEvaluate(node);
        foreach (var child in node.Children)
          inMemory &= MarkRunnableInCache(child);
        break;
    }
    // A value the caller brought needs no mark: it is a value wherever the query runs.
    if (inMemory && node.Tier != QueryTier.PreSql && node is not (ConstantNode or QueryParameterNode))
      node.TranslationFlags |= TranslationFlags.Cached;
    return inMemory;
  }

  /// <summary>Decides, where a Sql-tier node has a child this pass put on the PreSql tier, which way the boundary
  /// goes: the child becomes a parameter evaluated out of the run, or its whole chain goes back to the server.
  /// </summary>
  private void ResolveTierBoundaries(QueryNode node) {
    for (var i = 0; i < node.ChildCount; i++) {
      var child = node.Children[i];
      if (!child.TranslationFlags.HasFlag(TranslationFlags.Cached)) {
        ResolveTierBoundaries(child);
        continue;
      }
      // Back to the Sql tier: rows of an entity, anything standing where the operation wants a rowset, a lambda and a
      // value read off a row it binds, and the source of a mutation, since a parameter is one value per run.
      if (child is LambdaNode || ProducesEntityRows(child) || HasOuterScopeParameter(child)
          || node is MethodCallNode parent && (IsRowSetArgument(parent, i) || TranslationHelper.IsMutation(parent))) {
        ClearCached(child);
        continue;
      }
      // A value or a list of values computed over cached rows becomes a parameter evaluated out of the run.
      node.Children[i] = MakeCachedParameter(child, _cachedParameterCount++);
    }
  }

  #region Why an outer-scope parameter sends a cached child back to the server

  // An outer-scope parameter is a mention inside the child whose lambda sits above it, in an enclosing operation. With
  // `rates` a cached entity:
  //
  //   products.Where(p => p.Price > rates.Where(r => r.Code == p.Currency).Select(r => r.Rate).First())
  //
  // the cache pass marks the sub-query over `rates` Cached, since it reads cached rows only and the evaluator can run
  // it. Inside it, `r` is bound by the lambda `r => …`, which is inside the child; `p` is bound by the outer Where's
  // lambda, above the child, so it is of the outer scope. The child's value then depends on the row the outer query
  // is on: one value per row, not one per run, and no parameter can carry that, so the chain goes back to the server
  // and the sub-query is written as SQL after all. Without the `p.Currency` the child reads nothing from above and
  // becomes a parameter, the first matching rate computed once from the cached rows and bound into the statement.
  //
  // "Outer" is relative to the child asked about, which is why the bound set is collected per call and not once for
  // the tree; the walk covers the child alone, once per boundary, at translation.

  #endregion

  /// <summary>Collects the cached entities anything in the query reads in memory: a marked entity set in the tree,
  /// or one inside a sub-query a parameter now stands for.</summary>
  private List<ILinqEntity> CollectCachedEntities() {
    var entities = new List<ILinqEntity>();
    CollectCached(_root, entities, subQueriesOnly: false);
    return entities;
  }

  /// <summary>Collects the cached entities the statement reads in memory, through the sub-queries its parameters
  /// evaluate; a marked set in the tree itself is the whole query's, which the statement reads on the server.
  /// </summary>
  private List<ILinqEntity> CollectSqlCachedEntities() {
    var entities = new List<ILinqEntity>();
    CollectCached(_root, entities, subQueriesOnly: true);
    return entities;
  }

  private static void CollectCached(QueryNode node, List<ILinqEntity> entities, bool subQueriesOnly) {
    switch (node) {
      case EntitySetNode set when !subQueriesOnly && set.TranslationFlags.HasFlag(TranslationFlags.Cached)
          && !entities.Contains(set.Entity):
        entities.Add(set.Entity);
        break;
      case QueryParameterNode { Tier: QueryTier.PreSql, SubQueryTree: { } subtree }:
        CollectCached(subtree, entities, subQueriesOnly: false);
        break;
    }
    foreach (var child in node.Children)
      CollectCached(child, entities, subQueriesOnly);
  }

  /// <summary>Determines whether a PreSql node produces rows of an entity, which no parameter can carry, as opposed
  /// to a value or rows of values, which a list parameter can.</summary>
  private static bool ProducesEntityRows(QueryNode node) => node switch {
    MethodCallNode { MethodName: nameof(Queryable.Select) } select =>
      NodeEvaluatorBuilder.GetSelectedValue((LambdaNode)select.Argument(1)) is not { } value || value.GetEntity() != null,
    _ => node.GetEntity() != null,
  };

  /// <summary>Determines whether a child of a queryable call stands where the call wants rows: the source of every
  /// operation but <c>Contains</c>, whose first argument is a list; the far side of a join or a set operation.
  /// </summary>
  private static bool IsRowSetArgument(MethodCallNode call, int childIndex) {
    if (call.DeclaringType != typeof(Queryable) && call.DeclaringType != typeof(Enumerable))
      return false;
    return childIndex switch {
      0 => call.MethodName != nameof(Queryable.Contains),
      1 => call.MethodName is nameof(Queryable.Join) or nameof(Queryable.GroupJoin) or nameof(Queryable.Union)
        or nameof(Queryable.Concat) or nameof(Queryable.Intersect) or nameof(Queryable.Except),
      _ => false,
    };
  }

  /// <summary>Determines whether a subtree reads a lambda parameter it does not itself bind, a row of an operation
  /// above it.</summary>
  private static bool HasOuterScopeParameter(QueryNode node) {
    var bound = new HashSet<LambdaParameterNode>(ReferenceEqualityComparer.Instance);
    var fromOuterScope = false;
    node.ForEach(n => {
      if (n is LambdaNode lambda)
        for (var i = 0; i < lambda.ParameterCount; i++)
          bound.Add((LambdaParameterNode)lambda.Children[i]);
      else if (n is LambdaParameterNode parameter && !bound.Contains(parameter))
        fromOuterScope = true;
    });
    return fromOuterScope;
  }

  /// <summary>Takes the cache mark off a subtree that goes back to the server after all.</summary>
  private static void ClearCached(QueryNode node) {
    node.TranslationFlags &= ~TranslationFlags.Cached;
    foreach (var child in node.Children)
      ClearCached(child);
  }

  /// <summary>Makes the parameter that stands for a sub-query evaluated in memory, named by ordinal within the query
  /// since two sub-queries are two values.</summary>
  private static QueryParameterNode MakeCachedParameter(QueryNode value, int ordinal) {
    var evaluator = new NodeEvaluatorBuilder().Build(value);
    // A list where the sub-query produced rows of values, a value otherwise; typed by what was selected.
    var selected = value is MethodCallNode { MethodName: nameof(Queryable.Select) } select
      ? NodeEvaluatorBuilder.GetSelectedValue((LambdaNode)select.Argument(1)) : null;
    var type = selected?.DataType ?? value.DataType ?? typeof(object);
    var parameter = QueryMaker.Parameter($"cached{ordinal}", selected != null ? type.MakeArrayType() : type,
      callCtx => evaluator.Evaluate(callCtx));
    parameter.Tier = QueryTier.PreSql;
    parameter.ValueSource = ValueSourceKind.SubQuery;
    parameter.SubQueryTree = value;
    return parameter;
  }

  // ----- PostSql: what SQL cannot do, and everything above it ----------------------------------------------------------

  /// <summary>Marks what has to run after the SQL, in C#, over what the server returned, top-down from the first node
  /// SQL cannot express.</summary>
  /// <remarks>In a selector a PostSql node is projected around, so the select itself is not PostSql. On the rowset,
  /// such as a grouping returned as groups, the chain from that node to the root is PostSql. Inside a
  /// condition or an ordering it is refused by name.</remarks>
  private void AssignPostSql() {
    if (_root.Tier == QueryTier.PreSql)
      return;
    MarkPostSql(_root, QueryTreeArea.RowSet, groupsConsumed: false);
  }

  /// <summary>Specifies the area of the query tree a subtree stands in, which decides what a node SQL cannot express
  /// means there: on the rowset it is carried up to the root, in a selector it is projected around, in a condition it
  /// is refused.</summary>
  private enum QueryTreeArea {
    /// <summary>The rowset the operations are written over: a call's source, down to the set.</summary>
    RowSet,
    /// <summary>The lambda of a projection: a <c>Select</c>, a <c>SelectMany</c> or a <c>GroupBy</c>.</summary>
    Selector,
    /// <summary>The lambda of any other operation: a filter, an ordering, a join key, an aggregate's predicate.</summary>
    Condition,
  }

  /// <summary>Determines whether a subtree is PostSql, marking on the way and refusing where a PostSql node cannot
  /// stand.</summary>
  /// <param name="node">The root of the subtree.</param>
  /// <param name="area">The area of the tree the subtree stands in.</param>
  /// <param name="groupsConsumed"><see langword="true"/> if something above on the chain, such as a projection over
  /// the groups or an aggregate, turns groups into rows or a value, so that a grouping met below it is SQL's to do;
  /// otherwise, <see langword="false"/>.</param>
  private static bool MarkPostSql(QueryNode node, QueryTreeArea area, bool groupsConsumed) {
    if (node.Tier == QueryTier.PreSql)
      return false;
    var postSql = !IsSqlAble(node, groupsConsumed);
    if (node is MethodCallNode call
        && call.IsQueryableCall()) {
      // The source is the rowset; a lambda is a selector or a condition by the operation it is written for.
      var consumes = groupsConsumed || call.MethodName == nameof(Queryable.Select) || call.IsAggregateCall();
      postSql |= MarkPostSql(call.Argument(0), QueryTreeArea.RowSet, consumes);
      for (var i = 1; i < call.ArgumentCount; i++) {
        var argument = call.Argument(i);
        var insideArea = argument is LambdaNode ? GetLambdaArea(call) : area;
        var below = MarkPostSql(argument, insideArea, groupsConsumed: false);
        // A PostSql selector does not make the select PostSql: the select still runs, projecting what the C# needs.
        postSql |= below && insideArea != QueryTreeArea.Selector;
      }
    } else {
      foreach (var child in node.Children)
        postSql |= MarkPostSql(child, area, groupsConsumed);
      // A mutation has no "after the SQL": the server does the whole of it over the rows the query finds.
      if (postSql && TranslationHelper.IsMutation(node))
        throw new NotSupportedException(
          "The query under this mutation cannot be expressed in SQL, and a mutation is done by the server alone — "
          + "say the rows and the values with what SQL can say, or read the records and change them.");
    }
    if (postSql) {
      if (area == QueryTreeArea.Condition)
        throw new NotSupportedException(
          $"'{node}' cannot be expressed in SQL, and inside a condition or an ordering there is no answer short of "
          + "reading every row — compute it in C# first, or select it and filter what comes back.");
      node.Tier = QueryTier.PostSql;
    }
    return postSql;
  }

  /// <summary>Gets the area a lambda written for an operation stands in: the selector of a projection, a condition
  /// everywhere else.</summary>
  private static QueryTreeArea GetLambdaArea(MethodCallNode call) =>
    call.MethodName is nameof(Queryable.Select) or nameof(Queryable.SelectMany) or nameof(Queryable.GroupBy)
      ? QueryTreeArea.Selector
      : QueryTreeArea.Condition;

  /// <summary>Determines whether SQL can express a node itself; its children are asked separately.</summary>
  private static bool IsSqlAble(QueryNode node, bool groupsConsumed) => node switch {
    EntitySetNode or JsonRowsNode or TableFunctionNode or LambdaNode or LambdaParameterNode or ConstantNode
      or QueryParameterNode or NewNode => true,
    MemberAccessNode { ModelMember: not null } => true,
    // A slot of a bundle is the row or the expression it names, which the transform reads in its place.
    MemberAccessNode member when BundleShape.FindSlot(member) != null => true,
    // g.Key is the grouping's key expression, which the transform reads in its place.
    MemberAccessNode { MemberName: "Key", ModelMember: null, Target: { } group } when group.GetEntity() is GroupShape =>
      true,
    // A CLR member left over after the spellings pass is C#'s and not the server's.
    MemberAccessNode => false,
    BinaryNode or NamedOpNode or SetOperationNode => true,
    ExpressionNode { NodeType: ExpressionType.Not or ExpressionType.Convert or ExpressionType.ConvertChecked } => true,
    MethodCallNode call when call.IsQueryableCall() =>
      IsSqlQueryableCall(call, groupsConsumed),
    // A common table expression and a mutation are ways of writing the statement; a function's rows are the server's.
    MethodCallNode cte when TranslationHelper.IsCteCall(cte) => true,
    MethodCallNode function when TranslationHelper.IsTableFunctionCall(function) => true,
    MethodCallNode mutation when TranslationHelper.IsMutation(mutation) => true,
    _ => false,
  };

  private static bool IsSqlQueryableCall(MethodCallNode call, bool groupsConsumed) => call.MethodName switch {
    // A grouping returned as groups is done in C# over the source rows; under a projection or an aggregate it is
    // SQL's GROUP BY. A grouping built by name has no groups to return and is always SQL's.
    nameof(Queryable.GroupBy) => groupsConsumed || call.Source == null,
    nameof(Queryable.Where) or nameof(Queryable.OrderBy) or nameof(Queryable.OrderByDescending)
      or nameof(Queryable.ThenBy) or nameof(Queryable.ThenByDescending) or nameof(Queryable.Skip)
      or nameof(Queryable.Take) or nameof(Queryable.Distinct) or nameof(Queryable.Select)
      or nameof(Queryable.First) or nameof(Queryable.FirstOrDefault) or nameof(Queryable.Single)
      or nameof(Queryable.SingleOrDefault) or nameof(Queryable.Last) or nameof(Queryable.LastOrDefault)
      or nameof(Queryable.DefaultIfEmpty) or nameof(Queryable.Join) or nameof(Queryable.GroupJoin)
      or nameof(Queryable.SelectMany) or nameof(Queryable.Union) or nameof(Queryable.Concat)
      or nameof(Queryable.Intersect) or nameof(Queryable.Except) or nameof(Queryable.Any) or nameof(Queryable.All)
      or nameof(Queryable.Contains) or nameof(Queryable.Count) or nameof(Queryable.LongCount)
      or nameof(Queryable.Sum) or nameof(Queryable.Min) or nameof(Queryable.Max) or nameof(Queryable.Average) => true,
    _ => false,
  };

  /// <summary>Gets an expression with the conversions the compiler put around it taken off.</summary>
  /// <remarks>Since .NET 9, <c>array.Contains(x)</c> binds to a span overload, so the expression tree holds an implicit
  /// conversion from the array to a span, which cannot be boxed; taking it off gets back the array the caller wrote.
  /// </remarks>
  private static Expression UnwrapConversions(Expression expression) => expression switch {
    MethodCallExpression { Method.Name: "op_Implicit", Arguments.Count: 1 } conversion =>
      UnwrapConversions(conversion.Arguments[0]),
    UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert =>
      UnwrapConversions(convert.Operand),
    _ => expression,
  };

  /// <summary>Builds a name for the value an expression reads, stable for the same read, so that two reads of one
  /// field bind one parameter.</summary>
  private static string BuildParameterName(Expression expression) => expression switch {
    MemberExpression { Expression: ConstantExpression } member => member.Member.Name,
    MemberExpression { Expression: null } member => $"{member.Member.DeclaringType?.Name}_{member.Member.Name}",
    MemberExpression member => $"{BuildParameterName(member.Expression!)}_{member.Member.Name}",
    UnaryExpression unary => BuildParameterName(unary.Operand),
    MethodCallExpression { Object: { } target } call => $"{BuildParameterName(target)}_{call.Method.Name}",
    MethodCallExpression call => call.Method.Name,
    ConstantExpression constant => constant.Type.Name,
    _ => "value",
  };
}
