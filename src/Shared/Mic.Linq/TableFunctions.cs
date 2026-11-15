namespace Mic.Linq;

/// <summary>Provides the call of a function of the server that returns rows, written inside a query over the row
/// it reads: <c>from p in people from s in TableFunctions.Call&lt;IStop, Guid&gt;(p.Id)</c>.</summary>
/// <remarks>The translator writes the call as the server's lateral join where an argument reads the outer row, and
/// as a cross join where none does. The rows are of the entity a module declared as the function's; a call at the
/// root of a query is <c>session.TableFunction&lt;TRow&gt;(…)</c>. These methods throw when called outside a query.
/// </remarks>
public static class TableFunctions {
  /// <summary>Calls a function with no parameters.</summary>
  /// <typeparam name="TRow">The entity the function returns rows of.</typeparam>
  /// <returns>The rows.</returns>
  public static IQueryable<TRow> Call<TRow>() => throw MakeNotInQuery();

  /// <summary>Calls a function with one parameter.</summary>
  /// <typeparam name="TRow">The entity the function returns rows of.</typeparam>
  /// <typeparam name="T1">The type of the argument.</typeparam>
  /// <param name="argument1">The argument: a value of the outer row, a captured value or a constant.</param>
  /// <returns>The rows.</returns>
  public static IQueryable<TRow> Call<TRow, T1>(T1 argument1) => throw MakeNotInQuery();

  /// <summary>Calls a function with two parameters.</summary>
  /// <typeparam name="TRow">The entity the function returns rows of.</typeparam>
  /// <typeparam name="T1">The type of the first argument.</typeparam>
  /// <typeparam name="T2">The type of the second argument.</typeparam>
  /// <param name="argument1">The first argument.</param>
  /// <param name="argument2">The second argument.</param>
  /// <returns>The rows.</returns>
  public static IQueryable<TRow> Call<TRow, T1, T2>(T1 argument1, T2 argument2) => throw MakeNotInQuery();

  /// <summary>Calls a function with three parameters.</summary>
  /// <typeparam name="TRow">The entity the function returns rows of.</typeparam>
  /// <typeparam name="T1">The type of the first argument.</typeparam>
  /// <typeparam name="T2">The type of the second argument.</typeparam>
  /// <typeparam name="T3">The type of the third argument.</typeparam>
  /// <param name="argument1">The first argument.</param>
  /// <param name="argument2">The second argument.</param>
  /// <param name="argument3">The third argument.</param>
  /// <returns>The rows.</returns>
  public static IQueryable<TRow> Call<TRow, T1, T2, T3>(T1 argument1, T2 argument2, T3 argument3) => throw MakeNotInQuery();

  private static InvalidOperationException MakeNotInQuery() =>
    new($"{nameof(TableFunctions)}.{nameof(Call)} is written inside a query, where the server runs the function; "
      + "it does not run in memory.");
}
