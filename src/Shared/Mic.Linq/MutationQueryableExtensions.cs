using System.Linq.Expressions;
using System.Reflection;
using Mic.Linq.ExpressionTree;

namespace Mic.Linq;

/// <summary>Provides extension methods that run a query as a mutation: the rows it finds are deleted, updated with
/// the values it brings, or inserted by the server in one statement, with no record loaded. This class cannot be
/// inherited.</summary>
/// <remarks><para>A query written over the target entity itself names the rows by its filter. A query written over
/// any other entity brings the target's key as a projection, either one value or slots named for the key's members,
/// and names the target as a type argument. An update's projection also carries the values to write, in slots named
/// for the members they set.</para>
/// <para>The <c>Make…</c> methods build the marker expression without running it, for a host that holds the
/// statement to run later, as the ORM's session does for its next save.</para>
/// <para>A mutation is a statement over the table and nothing more. The filter every read carries still narrows the
/// rows, but no extension or save behaviour applies to them: a soft-delete entity is really deleted; an insert carries
/// no tenant, no <c>CreatedOn</c> and no generated key unless the projection brings them; an update may write any
/// member the projection names; no <c>ValidatingChanges</c> runs. Records the session tracks and cached blocks of the
/// entity are left as they were.</para></remarks>
public static class MutationQueryableExtensions {
  static readonly MethodInfo DeleteMethod = Find(nameof(Delete));
  static readonly MethodInfo DeleteByKeyMethod = Find(nameof(DeleteByKey));
  static readonly MethodInfo UpdateMethod = Find(nameof(Update));
  static readonly MethodInfo InsertMethod = Find(nameof(Insert));

  /// <summary>Asynchronously deletes the rows the query finds, which are rows of the entity the query is written
  /// over.</summary>
  /// <typeparam name="T">The entity type of the rows.</typeparam>
  /// <param name="source">The query that names the rows to delete.</param>
  /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
  /// <returns>A task that represents the asynchronous operation. The task result contains the number of rows
  /// deleted.</returns>
  /// <remarks>The rows must be the entity's own, such as a filtered, joined or paged set; a projection or a grouping
  /// has no table to delete from. A projection that brings a key is deleted by the
  /// <see cref="ExecuteDeleteAsync{TTarget}(IQueryable, CancellationToken)"/> overload.</remarks>
  public static Task<int> ExecuteDeleteAsync<T>(this IQueryable<T> source, CancellationToken cancellationToken = default) =>
    RunAsync(source, MakeDelete(source), cancellationToken);

  /// <summary>Asynchronously deletes the rows of <typeparamref name="TTarget"/> whose key the query brings.</summary>
  /// <typeparam name="TTarget">The entity type whose rows are deleted.</typeparam>
  /// <param name="source">A query over any entity that projects the target's key: one value for a single-column key,
  /// or slots named for the key's members.</param>
  /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
  /// <returns>A task that represents the asynchronous operation. The task result contains the number of rows
  /// deleted.</returns>
  /// <remarks>A query over another entity's rows without a projection is refused, since there is no key to take.
  /// </remarks>
  public static Task<int> ExecuteDeleteAsync<TTarget>(this IQueryable source, CancellationToken cancellationToken = default)
      where TTarget : class =>
    RunAsync(source, MakeDeleteByKey<TTarget>(source), cancellationToken);

  /// <summary>Asynchronously updates the rows of <typeparamref name="TTarget"/> with the values the query brings.
  /// </summary>
  /// <typeparam name="TTarget">The entity type whose rows are updated.</typeparam>
  /// <param name="source">A query that projects slots named for the target's members, holding the values to write.
  /// A query not over the target's own rows also projects the key's members, which name the rows.</param>
  /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
  /// <returns>A task that represents the asynchronous operation. The task result contains the number of rows
  /// updated.</returns>
  /// <remarks>Over the target itself the query's filter names the rows and the key need not be projected. Over any
  /// other entity the key slots find the rows and the value slots are read out of the query. A key slot is never
  /// written; a slot naming no member of the target is refused.</remarks>
  public static Task<int> ExecuteUpdateAsync<TTarget>(this IQueryable source, CancellationToken cancellationToken = default)
      where TTarget : class =>
    RunAsync(source, MakeUpdate<TTarget>(source), cancellationToken);

  /// <summary>Asynchronously inserts the rows the query brings into <typeparamref name="TTarget"/> as one
  /// <c>INSERT … SELECT</c> statement.</summary>
  /// <typeparam name="TTarget">The entity type the rows are inserted into.</typeparam>
  /// <param name="source">A query that projects slots named for the target's members.</param>
  /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
  /// <returns>A task that represents the asynchronous operation. The task result contains the number of rows
  /// inserted.</returns>
  /// <remarks>What is not projected is not written: a key the server generates is left to it, a column with a default
  /// takes it, and a required column left out is refused by the server. No extension or automatic value fills
  /// anything in; a tenant column, a <c>CreatedOn</c> or a generated Guid is the projection's to bring. A reference
  /// is written by the row it points at, whose key becomes the foreign key's columns.</remarks>
  public static Task<int> ExecuteInsertAsync<TTarget>(this IQueryable source, CancellationToken cancellationToken = default)
      where TTarget : class =>
    RunAsync(source, MakeInsert<TTarget>(source), cancellationToken);

  /// <summary>Marks, in a query expression, that the rows of <paramref name="source"/> are deleted.</summary>
  /// <typeparam name="T">The entity type of the rows.</typeparam>
  /// <param name="source">The query that names the rows.</param>
  /// <returns>This method does not return a value.</returns>
  /// <remarks>The marker is written into an expression for the translator to read; it throws when called.</remarks>
  public static int Delete<T>(IQueryable<T> source) => throw MakeNotCallable();

  /// <summary>Marks, in a query expression, that the rows of <typeparamref name="TTarget"/> whose key
  /// <paramref name="keys"/> brings are deleted.</summary>
  /// <typeparam name="TTarget">The entity type whose rows are deleted.</typeparam>
  /// <param name="keys">The query that projects the keys.</param>
  /// <returns>This method does not return a value.</returns>
  /// <remarks>The marker is written into an expression for the translator to read; it throws when called.</remarks>
  public static int DeleteByKey<TTarget>(IQueryable keys) => throw MakeNotCallable();

  /// <summary>Marks, in a query expression, that the rows of <typeparamref name="TTarget"/> are updated with the
  /// values <paramref name="values"/> brings.</summary>
  /// <typeparam name="TTarget">The entity type whose rows are updated.</typeparam>
  /// <param name="values">The query that projects the values.</param>
  /// <returns>This method does not return a value.</returns>
  /// <remarks>The marker is written into an expression for the translator to read; it throws when called.</remarks>
  public static int Update<TTarget>(IQueryable values) => throw MakeNotCallable();

  /// <summary>Marks, in a query expression, that the rows <paramref name="rows"/> brings are inserted into
  /// <typeparamref name="TTarget"/>.</summary>
  /// <typeparam name="TTarget">The entity type the rows are inserted into.</typeparam>
  /// <param name="rows">The query that projects the rows.</param>
  /// <returns>This method does not return a value.</returns>
  /// <remarks>The marker is written into an expression for the translator to read; it throws when called.</remarks>
  public static int Insert<TTarget>(IQueryable rows) => throw MakeNotCallable();

  /// <summary>Builds the expression that deletes the rows the query finds, without running it.</summary>
  public static Expression MakeDelete<T>(IQueryable<T> source) {
    ArgumentNullException.ThrowIfNull(source);
    return Expression.Call(null, DeleteMethod.MakeGenericMethod(typeof(T)), source.Expression);
  }

  /// <summary>Builds the expression that deletes the rows of <typeparamref name="TTarget"/> whose key the query
  /// brings, without running it.</summary>
  public static Expression MakeDeleteByKey<TTarget>(IQueryable source) {
    ArgumentNullException.ThrowIfNull(source);
    return Expression.Call(null, DeleteByKeyMethod.MakeGenericMethod(typeof(TTarget)), source.Expression);
  }

  /// <summary>Builds the expression that updates the rows of <typeparamref name="TTarget"/> with the values the
  /// query brings, without running it.</summary>
  public static Expression MakeUpdate<TTarget>(IQueryable source) {
    ArgumentNullException.ThrowIfNull(source);
    return Expression.Call(null, UpdateMethod.MakeGenericMethod(typeof(TTarget)), source.Expression);
  }

  /// <summary>Builds the expression that inserts the rows the query brings into <typeparamref name="TTarget"/>,
  /// without running it.</summary>
  public static Expression MakeInsert<TTarget>(IQueryable source) {
    ArgumentNullException.ThrowIfNull(source);
    return Expression.Call(null, InsertMethod.MakeGenericMethod(typeof(TTarget)), source.Expression);
  }

  private static async Task<int> RunAsync(IQueryable source, Expression expression, CancellationToken cancellationToken) {
    var result = await GetExecutor(source).ExecuteAsync(expression, cancellationToken);
    return Convert.ToInt32(result);
  }

  private static IQueryExecutor GetExecutor(IQueryable source) =>
    source.Provider is EntityQueryProvider provider
      ? provider.Executor
      : throw new ArgumentException($"This is not a MIC query — its provider is {source.Provider.GetType().Name}.", nameof(source));

  private static InvalidOperationException MakeNotCallable() =>
    new("A mutation is a statement the translator writes, and its marker cannot be called.");

  private static MethodInfo Find(string name) =>
    typeof(MutationQueryableExtensions).GetMethods(BindingFlags.Public | BindingFlags.Static).Single(m => m.Name == name);
}
