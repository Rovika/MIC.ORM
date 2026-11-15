using System.Linq.Expressions;
using Mic.Common;
namespace Mic.Linq.ExpressionTree;

/// <summary>Recognises a local variable holding a query read inside a lambda, which is how a sub-query is written.
///</summary>
internal static class LocalQueryHelper {
  /// <summary>Determines whether a member reads a query out of a captured object, and gets the query if so.</summary>
  /// <param name="node">The member access.</param>
  /// <param name="query">When this method returns, contains the query, or <see langword="null"/> if the member reads
  /// none.</param>
  /// <returns><see langword="true"/> if the member reads a query; otherwise, <see langword="false"/>.</returns>
  public static bool TryGetQuery(MemberExpression node, out IQueryable query) {
    if (node.Expression is ConstantExpression && typeof(IQueryable).IsAssignableFrom(node.Type)
        && ExpressionHelper.Evaluate(node) is IQueryable queryable) {
      query = queryable;
      return true;
    }
    query = null!;
    return false;
  }
}
