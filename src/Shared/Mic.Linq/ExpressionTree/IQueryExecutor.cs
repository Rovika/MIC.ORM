using System.Linq.Expressions;
namespace Mic.Linq.ExpressionTree;

/// <summary>Defines what runs a typed query: the ORM's session, or on the client whatever answers from the cache.
/// </summary>
/// <remarks>The result is a list of entity instances, a list of values, a list of projected rows, or one value; never
/// a record. The executor is the only part of the typed path that knows about records, sessions or databases.</remarks>
public interface IQueryExecutor {
  /// <summary>Asynchronously runs a query expression.</summary>
  /// <param name="expression">The query expression, ending in a terminal call.</param>
  /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
  /// <returns>A task that represents the asynchronous operation. The task result contains the rows, or the one
  /// value.</returns>
  Task<object?> ExecuteAsync(Expression expression, CancellationToken cancellationToken);
}
