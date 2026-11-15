using System.Linq.Expressions;
using Mic.Common;
using Mic.Linq.Nodes;
using Mic.Linq.Translation;

namespace Mic.Linq.ExpressionTree;

/// <summary>Scans a typed query's expression in one walk that only hashes, producing the cache key and the captured
/// objects the compiler put in the tree.</summary>
/// <remarks>The scan runs on every execution of a typed query; nothing is built, bound or read out of a closure.
/// The key is hashed from shape only: node types, member names, method names, the entity a set names and the value
/// of a literal, never the contents of a captured object. The scanner alone assigns argument positions, which the
/// translation looks up.</remarks>
public sealed class ExpressionTreeScanner : ExpressionWalker {
  readonly List<object?> _localClosures = [];
  QueryOptions _options;
  IRowSet? _root;
  ulong _hash = CacheKeySeeds.ExpressionTreeSeed;

  private ExpressionTreeScanner() { }

  /// <summary>Scans an expression.</summary>
  /// <param name="expression">The expression of a typed query.</param>
  /// <returns>The query's info: the cache key, the captured objects as the arguments, the options and the root set.
  /// </returns>
  public static QueryInfo Scan(Expression expression) {
    ArgumentNullException.ThrowIfNull(expression);
    var scanner = new ExpressionTreeScanner();
    scanner.Walk(expression);
    // Zero is reserved as the query cache's "nothing here".
    return new QueryInfo(scanner._hash == 0 ? 1 : scanner._hash, scanner._localClosures, scanner._options,
      scanner._root, isTyped: true);
  }

  protected override void Walk(Expression? expression) {
    if (expression == null)
      return;
    Add((ulong)expression.NodeType);
    base.Walk(expression);
  }

  protected override void WalkMember(MemberExpression node) {
    Add(node.Member.Name);

    // A local variable holding a query is walked as if written in place, as the converter copies it.
    if (LocalQueryHelper.TryGetQuery(node, out var query)) {
      Walk(query.Expression);
      return;
    }

    // A constant under a member access is a captured object: its type is shape, its contents are the value and must
    // not be hashed.
    if (node.Expression is ConstantExpression constExpr) {
      Add(constExpr.Type.FullName ?? constExpr.Type.Name);
      Register(constExpr.Value);
      return;
    }

    // A static member has no target to identify it, so its declaring type goes in.
    if (node.Expression == null) {
      Add(node.Member.DeclaringType?.FullName ?? "");
      return;
    }
    base.WalkMember(node);
  }

  protected override void WalkCall(MethodCallExpression node) {
    // The declaring type tells an application's own Where from Queryable's; the type arguments tell a mutation's
    // target.
    Add(node.Method.DeclaringType?.FullName ?? "");
    Add(node.Method.Name);
    if (node.Method.IsGenericMethod)
      foreach (var argument in node.Method.GetGenericArguments())
        Add(argument.FullName ?? argument.Name);
    base.WalkCall(node);
  }

  protected override void WalkParameter(ParameterExpression node) {
    // The name, so that (a, b) => a.X > b.X and (a, b) => b.X > a.X do not hash alike.
    Add(node.Name ?? "");
  }

  protected override void WalkConstant(ConstantExpression node) {
    switch (node.Value) {
      // A set contributes its entity by name, so that a rebuilt model still finds the statements built against the
      // old one.
      case IRowSet rowSet when rowSet.RowEntity is { } entity:
        Add(entity.Name);
        // The query's own root is the first rowSet met, since a call walks its source before its lambdas.
        _root ??= rowSet;
        // A rowSet that stands for a query contributes the key it already has, and is itself an argument.
        if (rowSet.CacheKey != 0) {
          Add(rowSet.CacheKey);
          Register(rowSet);
        }
        // Options are merged across sources. The locks are hashed, since they are in the statement's text; the rest
        // change which artifact is used, not what the query says.
        _options |= rowSet.Options;
        var locks = rowSet.Options & (QueryOptions.LockForUpdate | QueryOptions.SkipLocked);
        if (locks != 0)
          Add((ulong)locks);
        return;
      case null:
        Add(0UL);
        return;
      default:
        // A constant that survives to here is a literal, part of the query's text; anything that varies was hoisted
        // into a display class and is reached through a member access.
        Add(node.Value.ToString() ?? "");
        return;
    }
  }

  private void Register(object? localClosure) {
    foreach (var known in _localClosures)
      if (ReferenceEquals(known, localClosure))
        return;
    _localClosures.Add(localClosure);
  }

  private void Add(string text) => _hash = Fnv1aHash.Combine(_hash, text);

  private void Add(ulong value) => _hash = Fnv1aHash.Combine(_hash, value);
}
