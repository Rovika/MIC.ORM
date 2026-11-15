using System.Collections;
using System.Linq.Expressions;
using Mic.Linq;
using Mic.Linq.ExpressionTree;
using Mic.Linq.Model;
using Mic.Linq.Nodes;

namespace Mic.Orm;

/// <summary>Provides the entity sets a view's defining query is written over, outside any session.</summary>
/// <remarks>The query is a definition: it is translated into the view's SQL when a db model is built, and never run.
/// </remarks>
public static class ViewHelper {
  /// <summary>Gets the set of an entity's rows, to write a view's defining query over.</summary>
  /// <typeparam name="T">The entity interface.</typeparam>
  /// <returns>A queryable to compose the query over.</returns>
  public static IQueryable<T> EntitySet<T>() where T : class => new ViewEntitySet<T>();
}

/// <summary>Represents an entity set in a view's defining query: it names the entity by its interface, and is bound
/// to the model's entity when the view is built.</summary>
public abstract class ViewEntitySet : IRowSet {
  /// <summary>Gets the entity interface the set is all the rows of.</summary>
  public abstract Type EntityType { get; }

  /// <summary>Gets the entity the set is bound to, or <see langword="null"/> before the view is built.</summary>
  public ILinqEntity? RowEntity { get; private set; }

  QueryOptions IRowSet.Options => QueryOptions.None;

  ulong IRowSet.CacheKey => 0;

  /// <inheritdoc/>
  public QueryNode GetNode() =>
    QueryMaker.From(RowEntity ?? throw new InvalidOperationException(
      $"The set of '{EntityType.Name}' is not bound to a model; a view's query is translated when its db model is built."));

  /// <summary>Binds the set to the model's entity of its interface.</summary>
  internal void Bind(ILinqEntity entity) => RowEntity = entity;
}

/// <summary>Represents the set of an entity's rows in a view's defining query. This class cannot be inherited.
/// </summary>
/// <typeparam name="T">The entity interface.</typeparam>
public sealed class ViewEntitySet<T> : ViewEntitySet, IOrderedQueryable<T> where T : class {
  static readonly EntityQueryProvider _provider = new(new ViewQueryExecutor());

  public ViewEntitySet() {
    Expression = Expression.Constant(this);
  }

  /// <inheritdoc/>
  public override Type EntityType => typeof(T);

  public Expression Expression { get; }

  public Type ElementType => typeof(T);

  IQueryProvider IQueryable.Provider => _provider;

  public IEnumerator<T> GetEnumerator() => throw ViewQueryExecutor.MakeNotRunnable();

  IEnumerator IEnumerable.GetEnumerator() => throw ViewQueryExecutor.MakeNotRunnable();

  public override string ToString() => $"{typeof(T).Name} set of a view";
}

/// <summary>Refuses to run a view's defining query, which is a definition and not a query to run.</summary>
sealed class ViewQueryExecutor : IQueryExecutor {
  public Task<object?> ExecuteAsync(Expression expression, CancellationToken cancellationToken) =>
    throw MakeNotRunnable();

  internal static InvalidOperationException MakeNotRunnable() =>
    new("A view's defining query is a definition and is never run — read the view through its entity in a session.");
}
