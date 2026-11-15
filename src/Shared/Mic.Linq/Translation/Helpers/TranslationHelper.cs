using System.Collections.Frozen;
using System.Linq.Expressions;
using Mic.Common;
using Mic.Linq.Model;
using Mic.Linq.Nodes;

namespace Mic.Linq.Translation;

/// <summary>Provides what every pass over a tree needs and no node owns: the walks, the entity a node's members
/// belong to, the shape a projection makes, and what tells a queryable call, a common table expression or a
/// mutation marker from any other call.</summary>
/// <remarks>This is the translator's toolbox, not the public surface; a query author uses <see cref="QueryMaker"/>.
/// The logical operators the translator composes conditions with are made by <see cref="MakeAnd"/> and its
/// siblings, never by the fluent form, which the compiler would bind to first inside this namespace.</remarks>
public static class TranslationHelper {
  // The queryable calls that fold their source into one value.
  static readonly FrozenSet<string> _aggregateCalls = FrozenSet.ToFrozenSet([
    nameof(Queryable.Count), nameof(Queryable.LongCount), nameof(Queryable.Any), nameof(Queryable.All),
    nameof(Queryable.Sum), nameof(Queryable.Min), nameof(Queryable.Max), nameof(Queryable.Average),
  ]);

  /// <summary>Gets the entity rows are rows of, behind a decorator the engine put over it for a grouping or a
  /// bundle; the entity itself otherwise.</summary>
  /// <param name="entity">The entity, possibly a decorator.</param>
  /// <returns>The entity behind the decorator, or <paramref name="entity"/> itself.</returns>
  public static ILinqEntity GetRowEntity(ILinqEntity entity) => entity switch {
    GroupShape group => group.Entity,
    BundleShape bundle => bundle.Entity,
    _ => entity,
  };

  /// <summary>Builds the entity a projection's rows are rows of: one member per selected value, under its name, made
  /// by the model.</summary>
  /// <param name="model">The model that makes the entity.</param>
  /// <param name="name">The name of the entity.</param>
  /// <param name="selected">The selected values, each named and typed.</param>
  /// <returns>The transient entity.</returns>
  /// <remarks>A member is nullable unless the model says otherwise; any other expression is nullable.</remarks>
  public static ILinqEntity BuildRowShape(ILinqDataModel model, string name, IReadOnlyList<QueryNode> selected) {
    ArgumentNullException.ThrowIfNull(model);
    ArgumentException.ThrowIfNullOrEmpty(name);
    ArgumentNullException.ThrowIfNull(selected);
    var members = new LinqMemberSpec[selected.Count];
    for (var i = 0; i < members.Length; i++) {
      var node = selected[i];
      var type = node.DataType
        ?? throw new NotSupportedException(
          $"Selected '{node.Name}' has no type, so there is no field to put it in.");
      var nullable = node is not MemberAccessNode { ModelMember: { } member } || member.IsNullable;
      members[i] = new LinqMemberSpec(node.Name!, type, nullable);
    }
    return model.CreateTransientEntity(name, members);
  }

  // ----- walking and hashing ------------------------------------------------------------------------------------------

  /// <summary>Specifies the greatest depth a tree may have, deeper than any real query tree.</summary>
  public const int MaxDepth = 256;

  /// <summary>Throws when a walk has gone deeper than <see cref="MaxDepth"/>, which a cycle in the tree would
  /// otherwise turn into a stack overflow.</summary>
  /// <param name="node">The node at the current depth.</param>
  /// <param name="depth">The current depth.</param>
  /// <remarks>Every recursion over a tree runs under this guard. A depth counter rather than a visited set, since the
  /// row node is shared on purpose.</remarks>
  public static void CheckDepth(this QueryNode node, int depth) {
    if (depth > MaxDepth)
      throw new InvalidOperationException(
        $"The query tree is deeper than {MaxDepth} nodes — a cycle, most likely. The node at the limit is a "
        + $"{node.Kind}{(node.Name is { Length: > 0 } ? $" named '{node.Name}'" : "")}.");
  }

  /// <summary>Visits every node of a subtree with a callback.</summary>
  /// <param name="node">The root of the subtree.</param>
  /// <param name="visit">The callback called for each node.</param>
  /// <param name="bottomUp"><see langword="true"/> to visit children before their parent, for a pass deriving a
  /// node's answer from its children; otherwise, <see langword="false"/> to visit a node before its children, for a
  /// pass carrying context downwards.</param>
  /// <remarks>Writing to a node's properties during the walk is allowed; replacing a node is what
  /// <see cref="Transform(QueryNode, Func{QueryNode, QueryNode}, bool)"/> is for. A <see cref="NodeKind.Row"/> node
  /// is shared and is visited once per reference.
  /// </remarks>
  public static void ForEach(this QueryNode node, Action<QueryNode> visit, bool bottomUp = false) {
    ArgumentNullException.ThrowIfNull(node);
    ArgumentNullException.ThrowIfNull(visit);
    ForEach(node, visit, bottomUp, 0);
  }

  private static void ForEach(QueryNode node, Action<QueryNode> visit, bool bottomUp, int depth) {
    node.CheckDepth(depth);
    if (!bottomUp)
      visit(node);
    if (node.HasChildren)
      for (var i = 0; i < node.Children.Count; i++)
        ForEach(node.Children[i], visit, bottomUp, depth + 1);
    if (bottomUp)
      visit(node);
  }

  /// <summary>Offers every node of a tree to a transform, and puts what comes back in its place in the parent.</summary>
  /// <param name="root">The root of the tree.</param>
  /// <param name="transform">A function that, given a node, returns the node to put in its place: the same one to leave
  /// it, another to replace it. It may also mutate the node it is given.</param>
  /// <param name="bottomUp"><see langword="true"/> to offer a node after its children, for a transform deriving a node's
  /// replacement from its transformed children; otherwise, <see langword="false"/> to offer a node before its children,
  /// so that the replacement is what gets walked next and a node expanded into a subtree has the subtree's children
  /// transformed too.</param>
  /// <returns>The root, which may itself have been replaced.</returns>
  /// <remarks>A node is transformed once however often it is referenced: the first visit decides what it becomes, and
  /// every later reference gets the same answer, which keeps a shared row's identity.</remarks>
  public static QueryNode Transform(this QueryNode root, Func<QueryNode, QueryNode> transform, bool bottomUp = false) {
    ArgumentNullException.ThrowIfNull(root);
    ArgumentNullException.ThrowIfNull(transform);
    var done = new Dictionary<QueryNode, QueryNode>(ReferenceEqualityComparer.Instance);
    return Transform(root, transform, bottomUp, done, 0);
  }

  private static QueryNode Transform(QueryNode node, Func<QueryNode, QueryNode> transform, bool bottomUp,
      Dictionary<QueryNode, QueryNode> done, int depth) {
    if (done.TryGetValue(node, out var already))
      return already;
    node.CheckDepth(depth);
    var result = node;
    if (!bottomUp) {
      result = transform(node) ?? throw new InvalidOperationException($"A transform returned null for a {node}.");
      // Recorded before the children are walked, so a reference to this node met further down resolves to the
      // replacement rather than starting a second transform of it.
      done[node] = result;
    }
    if (result.HasChildren)
      for (var i = 0; i < result.Children.Count; i++)
        result.Children[i] = Transform(result.Children[i], transform, bottomUp, done, depth + 1);
    if (bottomUp) {
      result = transform(node) ?? throw new InvalidOperationException($"A transform returned null for a {node}.");
      done[node] = result;
    }
    return result;
  }

  /// <summary>Gets what a subtree hashes to, which read from the root is the query cache's key.</summary>
  /// <param name="node">The root of the subtree.</param>
  /// <returns>The hash of the subtree's shape.</returns>
  /// <remarks>The key is memoised per node and never recomputed, so it must be read at translation and not while the
  /// tree is still being built. Only shape goes in: a parameter contributes its name and never its value; a constant
  /// contributes its value, since two constants are two statements. The hash starts from
  /// <see cref="CacheKeySeeds.QueryTreeHashSeed"/>.</remarks>
  public static ulong GetCacheKey(this QueryNode node) {
    ArgumentNullException.ThrowIfNull(node);
    return GetCacheKey(node, 0);
  }

  private static ulong GetCacheKey(QueryNode node, int depth) {
    if (node.CacheKey != 0)
      return node.CacheKey;
    node.CheckDepth(depth);
    var hash = Fnv1aHash.Combine(CacheKeySeeds.QueryTreeHashSeed, (ulong)node.Kind);
    hash = Fnv1aHash.Combine(hash, (ulong)node.Flags);
    if (node.Name != null)
      hash = Fnv1aHash.Combine(hash, node.Name);
    if (node.GetEntity() is { } entity)
      hash = Fnv1aHash.Combine(hash, entity.Name);
    hash = node.AddCustomNodeHash(hash);
    if (node.HasChildren)
      foreach (var child in node.Children)
        hash = Fnv1aHash.Combine(hash, GetCacheKey(child, depth + 1));
    // Zero marks a key not yet computed, so a key that lands on it takes the next value up.
    return node.CacheKey = hash == 0 ? 1 : hash;
  }

  /// <summary>Builds the layout of a tree's parameters: assigns an argument slot to each parameter that takes one,
  /// and returns them in slot order.</summary>
  /// <param name="root">The root of the tree.</param>
  /// <returns>The parameters, one per name, in the order the tree names them.</returns>
  /// <remarks>The order is the tree's, source before what is applied to it, not the statement's. Only an
  /// <see cref="ValueSourceKind.Argument"/> parameter takes a slot. The layout is built before any translation, since
  /// a transform replaces a parameter with ones that read the same argument.</remarks>
  public static IReadOnlyList<QueryParameterNode> BuildParametersLayout(this QueryNode root) {
    ArgumentNullException.ThrowIfNull(root);
    var layout = new List<QueryParameterNode>();
    root.ForEach(node => {
      if (node is not QueryParameterNode { ValueSource: ValueSourceKind.Argument } parameter)
        return;
      // A parameter made from a bare value takes a slot of its own and is named for it, the same for every tree of
      // the shape.
      if (parameter.IsAutoNamed) {
        parameter.AssignSlot(layout.Count);
        parameter.Name = "#" + layout.Count;
        layout.Add(parameter);
        return;
      }
      var first = layout.Find(p => p.Name == parameter.Name);
      if (first == null) {
        parameter.AssignSlot(layout.Count);
        layout.Add(parameter);
        return;
      }
      // One name is one value: written twice it is one slot; carrying two different values it is a mistake.
      if (!Equals(first.ArgValue, parameter.ArgValue))
        throw new InvalidOperationException(
          $"Parameter '{parameter.Name}' is written twice with different values. One name is one value — name them apart.");
      parameter.AssignSlot(first.ArgIndex);
    });
    return layout;
  }

  /// <summary>Gets the entity a node's members belong to: the entity a rowset produces rows of, a row is a row of,
  /// or a reference reaches.</summary>
  /// <param name="node">The node.</param>
  /// <returns>The entity, or <see langword="null"/> for a value.</returns>
  public static ILinqEntity? GetEntity(this QueryNode node) => node switch {
    RowSetNode set => set.Entity,
    RowNode row => row.Entity,
    // What the binder recorded; for a member made after binding, what a reference reaches, or for a member of a
    // bundle, a projection holding whole rows resolved away, the slot it names.
    MemberAccessNode member => member.Entity
      ?? (member.ModelMember != null ? member.TargetEntity : BundleShape.FindSlot(member)?.GetEntity()),
    MethodCallNode call => call.Entity,
    LambdaParameterNode parameter => parameter.Entity,
    _ => null,
  };

  /// <summary>Determines whether a condition reads the operation context, and so answers differently for two
  /// callers.</summary>
  /// <param name="condition">The condition.</param>
  /// <returns><see langword="true"/> if a parameter in the condition reads the context; otherwise,
  /// <see langword="false"/>.</returns>
  public static bool ReadsContext(this QueryNode condition) {
    ArgumentNullException.ThrowIfNull(condition);
    var readsContext = false;
    condition.ForEach(node => readsContext |= node is QueryParameterNode { ValueSource: ValueSourceKind.Context });
    return readsContext;
  }

  /// <summary>Gets the lambda a call carries as an argument.</summary>
  /// <param name="call">The call.</param>
  /// <param name="argument">The position of the argument.</param>
  /// <returns>The lambda.</returns>
  /// <exception cref="NotSupportedException">The argument is not a lambda.</exception>
  public static LambdaNode GetLambda(this MethodCallNode call, int argument) =>
    call.Argument(argument) as LambdaNode
      ?? throw new NotSupportedException(
        $"Argument {argument} of {call.MethodName} is a '{call.Argument(argument).Kind}' where a lambda was expected.");

  /// <summary>Determines whether a call is one of LINQ's own, declared on <c>Queryable</c> or <c>Enumerable</c>, and
  /// so a queryable call rather than a method of some other type.</summary>
  /// <param name="call">The call.</param>
  /// <returns><see langword="true"/> if the call is a queryable call; otherwise, <see langword="false"/>.</returns>
  public static bool IsQueryableCall(this MethodCallNode call) {
    ArgumentNullException.ThrowIfNull(call);
    return call.DeclaringType == typeof(Queryable) || call.DeclaringType == typeof(Enumerable);
  }

  /// <summary>Determines whether a call is a queryable call that folds its source into one value: a count, an
  /// <c>Any</c> or <c>All</c>, a sum, a minimum, a maximum or an average.</summary>
  /// <param name="call">The call.</param>
  /// <returns><see langword="true"/> if the call is an aggregate; otherwise, <see langword="false"/>.</returns>
  public static bool IsAggregateCall(this MethodCallNode call) =>
    call.IsQueryableCall() && _aggregateCalls.Contains(call.MethodName);

  /// <summary>Determines whether a node asks for one row, as one of the six <c>First</c>, <c>Single</c> and
  /// <c>Last</c> calls.</summary>
  /// <param name="node">The node.</param>
  /// <returns><see langword="true"/> if the node is one of those calls; otherwise, <see langword="false"/>.</returns>
  public static bool IsFirstLastSingle(this QueryNode node) =>
    node is MethodCallNode { MethodName: nameof(Queryable.First) or nameof(Queryable.FirstOrDefault)
      or nameof(Queryable.Single) or nameof(Queryable.SingleOrDefault)
      or nameof(Queryable.Last) or nameof(Queryable.LastOrDefault) };

  // ----- the framework's own calls in a tree — the `WITH` markers and the mutation markers ------------------------------

  /// <summary>Determines whether a call is <c>AsCte</c> or <c>AsRecursiveCte</c>.</summary>
  internal static bool IsCteCall(MethodCallNode call) {
    ArgumentNullException.ThrowIfNull(call);
    return call.DeclaringType == typeof(CteQueryableExtensions);
  }

  /// <summary>Determines whether a call is <c>TableFunctions.Call</c>, the rows of a function of the server written
  /// inside a query.</summary>
  internal static bool IsTableFunctionCall(MethodCallNode call) {
    ArgumentNullException.ThrowIfNull(call);
    return call.DeclaringType == typeof(TableFunctions);
  }

  /// <summary>Determines whether a node is an <c>AsCte</c> call.</summary>
  internal static bool IsAsCte(QueryNode node) =>
    node is MethodCallNode { MethodName: nameof(CteQueryableExtensions.AsCte) } call && IsCteCall(call);

  /// <summary>Gets the statement the node at the top of the SQL tier is: the mutation a marker stands for, or a select
  /// for any other node.</summary>
  /// <remarks>The delete markers are <c>Delete</c>, of its source's rows, and <c>DeleteByKey</c>, of the rows of the
  /// target its type argument names.</remarks>
  internal static StatementKind GetStatementKind(QueryNode root) {
    ArgumentNullException.ThrowIfNull(root);
    if (root is not MethodCallNode call || !IsMutationMarker(call))
      return StatementKind.Select;
    return call.MethodName switch {
      nameof(MutationQueryableExtensions.Delete) or nameof(MutationQueryableExtensions.DeleteByKey) => StatementKind.Delete,
      nameof(MutationQueryableExtensions.Update) => StatementKind.Update,
      nameof(MutationQueryableExtensions.Insert) => StatementKind.Insert,
      _ => throw new NotSupportedException($"'{call.MethodName}' is no mutation marker."),
    };
  }

  /// <summary>Determines whether a mutation marker names its target as a type argument, which every marker but the
  /// delete of a source's own rows does.</summary>
  internal static bool NamesMutationTarget(MethodCallNode call) => call.MethodName != nameof(MutationQueryableExtensions.Delete);

  /// <summary>Gets the CLR type a mutation marker names its target as.</summary>
  internal static Type GetMutationTargetType(MethodCallNode call) =>
    call.Method?.GetGenericArguments()[0]
    ?? throw new InvalidOperationException($"'{call}' names no target type — it was not written by the typed path.");

  /// <summary>Determines whether a node is any mutation marker.</summary>
  internal static bool IsMutation(QueryNode node) => node is MethodCallNode call && IsMutationMarker(call);

  private static bool IsMutationMarker(MethodCallNode call) => call.DeclaringType == typeof(MutationQueryableExtensions);

  // ----- making the SQL tree's nodes, and taking the includes off a root --------------------------------------------

  /// <summary>Makes a binary node over two operands, typed as a Boolean for a comparison and by the left operand for
  /// arithmetic.</summary>
  /// <param name="type">The operator.</param>
  /// <param name="left">The left operand.</param>
  /// <param name="right">The right operand.</param>
  /// <returns>The new node.</returns>
  /// <remarks>The arithmetic type is refined at statement assembly, where promotion between numeric types is the
  /// vendor's.</remarks>
  public static BinaryNode MakeBinary(ExpressionType type, QueryNode left, QueryNode right) {
    ArgumentNullException.ThrowIfNull(left);
    ArgumentNullException.ThrowIfNull(right);
    var node = new BinaryNode(type) { Children = { left, right } };
    node.DataType = node.IsComparison ? typeof(bool) : left.DataType ?? right.DataType;
    return node;
  }

  /// <summary>Makes the conjunction of the operands as one node, opening up any <c>And</c> among them.</summary>
  /// <param name="operands">An array that contains the operands, at least two.</param>
  /// <returns>A node that is true when every operand is.</returns>
  public static QueryNode MakeAnd(params QueryNode[] operands) => MakeFlat(LogicalOpType.And, operands);

  /// <summary>Makes the disjunction of the operands as one node, opening up any <c>Or</c> among them.</summary>
  /// <param name="operands">An array that contains the operands, at least two.</param>
  /// <returns>A node that is true when any operand is.</returns>
  public static QueryNode MakeOr(params QueryNode[] operands) => MakeFlat(LogicalOpType.Or, operands);

  /// <summary>Makes the negation of a condition.</summary>
  /// <param name="operand">The condition.</param>
  /// <returns>A node that is true when the condition is false.</returns>
  public static QueryNode MakeNot(QueryNode operand) {
    ArgumentNullException.ThrowIfNull(operand);
    return new LogicalOpNode(LogicalOpType.Not, operand);
  }

  private static QueryNode MakeFlat(LogicalOpType opType, QueryNode[] operands) {
    ArgumentNullException.ThrowIfNull(operands);
    if (operands.Length < 2)
      throw new ArgumentException($"{opType} needs at least two operands.", nameof(operands));
    var node = new LogicalOpNode(opType);
    foreach (var operand in operands) {
      ArgumentNullException.ThrowIfNull(operand);
      if (operand is LogicalOpNode nested && nested.OpType == opType)
        node.Children.AddRange(operand.Children);
      else
        node.AddChild(operand);
    }
    return node;
  }

  /// <summary>Takes the include nodes out of a tree wherever they stand, each replaced by the query under it, and
  /// collects their includes.</summary>
  /// <param name="root">The root of the tree.</param>
  /// <param name="includes">When this method returns, contains the includes in the order they were written, the
  /// innermost node's first.</param>
  /// <returns>The tree without its include nodes: the root's own query where the root was one.</returns>
  /// <remarks>An include is usually the outermost call, but one written before a filter or an ordering is as good;
  /// what it names is loaded with the rows either way.</remarks>
  public static QueryNode StripIncludes(QueryNode root, out IReadOnlyList<QueryInclude> includes) =>
    StripWrappers(root, out includes, out _, out _);

  /// <summary>Takes the include nodes and the ignore-filters nodes off a tree, collecting what they said.</summary>
  /// <param name="root">The root of the tree.</param>
  /// <param name="includes">When this method returns, contains the includes in the order they were written, the
  /// innermost node's first.</param>
  /// <param name="ignoresAllFilters">When this method returns, <see langword="true"/> if a node dropped every
  /// extension's filters; otherwise, <see langword="false"/>.</param>
  /// <param name="ignoredFilterOwners">When this method returns, contains the extensions whose filters nodes
  /// dropped.</param>
  /// <returns>The tree without its wrapper nodes: the root's own query where the root was one.</returns>
  public static QueryNode StripWrappers(QueryNode root, out IReadOnlyList<QueryInclude> includes,
      out bool ignoresAllFilters, out IReadOnlyList<Type> ignoredFilterOwners) {
    ArgumentNullException.ThrowIfNull(root);
    var collected = new List<QueryInclude>();
    var ignoreAll = false;
    var owners = new List<Type>();
    // Top-down, the outer wrapper first; a stack of them is unwrapped here, since a replacement is not offered again.
    var stripped = root.Transform(node => {
      while (true) {
        if (node is IncludeNode include) {
          collected.InsertRange(0, include.Includes);
          node = include.Source;
        } else if (node is IgnoreFiltersNode ignore) {
          if (ignore.ExtensionType == null)
            ignoreAll = true;
          else
            owners.Add(ignore.ExtensionType);
          node = ignore.Source;
        } else {
          return node;
        }
      }
    });
    includes = collected;
    ignoresAllFilters = ignoreAll;
    ignoredFilterOwners = owners;
    return stripped;
  }
}
