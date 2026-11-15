using Mic.Orm.Db;
using Mic.Orm.Db.Readers;
using Mic.Orm.Runtime;

namespace Mic.Orm;

/// <summary>Provides the methods that run a hand-written statement through a session, so that it gets the session's
/// connection and open transaction, the log, <c>LastCommands</c>, the timing and the failure classification.</summary>
/// <remarks>A parameter is written <c>@name</c> in the text, as SQL Server, MySQL, PostgreSQL and SQLite take it, and
/// converted to the provider's prefix where it differs, as Oracle's <c>:name</c>; on MySQL an <c>@name</c> with no value
/// supplied is a user variable and is left as written. The values go by
/// name. A statement says nothing to the session: the records stay as they were read and the entity cache keeps its
/// blocks, unless the caller names the entities the statement wrote through the <c>touches</c> of
/// <c>RunSqlNonQueryAsync</c>.</remarks>
public static class DirectSqlExtensions {
  /// <summary>Asynchronously runs a select and returns its rows as they came: column names and values, a value by
  /// column name.</summary>
  /// <param name="session">The session to run the statement through.</param>
  /// <param name="sql">The statement, with parameters written <c>@name</c>.</param>
  /// <param name="parameters">The parameter values, by name.</param>
  /// <returns>A task that represents the asynchronous operation. The task result contains the rows.</returns>
  public static async Task<InfoTable> RunSqlQueryAsync(this IEntitySession session, string sql,
      params (string Name, object? Value)[] parameters) =>
    (InfoTable)(await RunSqlRequestAsync(session, sql, parameters, DbExecutionType.Reader, BufferedRowsReader.Instance))!;

  /// <summary>Asynchronously runs a select and hands the open data reader to a result reader.</summary>
  /// <param name="session">The session to run the statement through.</param>
  /// <param name="sql">The statement, with parameters written <c>@name</c>.</param>
  /// <param name="resultReader">The result reader.</param>
  /// <param name="parameters">The parameter values, by name.</param>
  /// <returns>A task that represents the asynchronous operation. The task result contains what the result reader
  /// returned.</returns>
  public static async Task<object?> RunSqlQueryAsync(this IEntitySession session, string sql,
      IResultReader resultReader, params (string Name, object? Value)[] parameters) {
    ArgumentNullException.ThrowIfNull(resultReader);
    return await RunSqlRequestAsync(session, sql, parameters, DbExecutionType.Reader, resultReader);
  }

  /// <summary>Asynchronously runs a select and reads every row as a type named at run time.</summary>
  /// <param name="session">The session to run the statement through.</param>
  /// <param name="sql">The statement, with parameters written <c>@name</c>.</param>
  /// <param name="readAs">The type to read each row as: an entity interface of the model gives tracked records, a
  /// scalar type the first column of every row, any other class or record one instance per row built by column
  /// name.</param>
  /// <param name="parameters">The parameter values, by name.</param>
  /// <returns>A task that represents the asynchronous operation. The task result contains the list the executor
  /// built; see <see cref="DataRequest.ReadResultAs"/>.</returns>
  public static Task<object?> RunSqlQueryAsync(this IEntitySession session, string sql, Type readAs,
      params (string Name, object? Value)[] parameters) {
    ArgumentNullException.ThrowIfNull(readAs);
    return RunSqlRequestAsync(session, sql, parameters, DbExecutionType.Reader, readAs: readAs);
  }

  /// <summary>Asynchronously runs a select and reads every row as <typeparamref name="T"/>.</summary>
  /// <typeparam name="T">The type to read each row as; an entity's records come back as their instances.</typeparam>
  /// <param name="session">The session to run the statement through.</param>
  /// <param name="sql">The statement, with parameters written <c>@name</c>.</param>
  /// <param name="parameters">The parameter values, by name.</param>
  /// <returns>A task that represents the asynchronous operation. The task result contains the typed list.</returns>
  public static async Task<IReadOnlyList<T>> RunSqlQueryAsync<T>(this IEntitySession session, string sql,
      params (string Name, object? Value)[] parameters) {
    var result = await RunSqlQueryAsync(session, sql, typeof(T), parameters);
    return result switch {
      List<T> typed => typed,
      IReadOnlyList<EntityRecord> records => records.Select(r => (T)(object)r?.EntityInstance!).ToList(),
      List<object?> values => values.Select(v => v == null ? default! : (T)v).ToList(),
      _ => [],
    };
  }

  /// <summary>Asynchronously runs a statement for one value, the first column of its first row.</summary>
  /// <typeparam name="T">The type to convert the value to.</typeparam>
  /// <param name="session">The session to run the statement through.</param>
  /// <param name="sql">The statement, with parameters written <c>@name</c>.</param>
  /// <param name="parameters">The parameter values, by name.</param>
  /// <returns>A task that represents the asynchronous operation. The task result contains the value, or
  /// <see langword="null"/> if there is no row or the value is <see langword="null"/>.</returns>
  public static async Task<T?> RunSqlScalarAsync<T>(this IEntitySession session, string sql,
      params (string Name, object? Value)[] parameters) {
    var entSession = EntitySessionHelper.GetEntitySession(session);
    var value = await RunSqlRequestAsync(session, sql, parameters, DbExecutionType.Scalar);
    return value == null || value == DBNull.Value ? default : (T?)entSession.Database.Provider.ConvertReadValue(value, typeof(T));
  }

  /// <summary>Asynchronously runs a statement for its row count: an update, a delete, an insert, a call.</summary>
  /// <param name="session">The session to run the statement through.</param>
  /// <param name="sql">The statement, with parameters written <c>@name</c>.</param>
  /// <param name="parameters">The parameter values, by name.</param>
  /// <returns>A task that represents the asynchronous operation. The task result contains the row count.</returns>
  public static Task<int> RunSqlNonQueryAsync(this IEntitySession session, string sql,
      params (string Name, object? Value)[] parameters) =>
    RunSqlNonQueryAsync(session, sql, [], parameters);

  /// <summary>Asynchronously runs a statement for its row count and drops the cached blocks of the entities whose
  /// rows it wrote.</summary>
  /// <param name="session">The session to run the statement through.</param>
  /// <param name="sql">The statement, with parameters written <c>@name</c>.</param>
  /// <param name="touches">The entity types whose rows the statement writes, which the caller knows and the ORM does
  /// not.</param>
  /// <param name="parameters">The parameter values, by name.</param>
  /// <returns>A task that represents the asynchronous operation. The task result contains the row count.</returns>
  public static async Task<int> RunSqlNonQueryAsync(this IEntitySession session, string sql, IReadOnlyList<Type> touches,
      params (string Name, object? Value)[] parameters) {
    ArgumentNullException.ThrowIfNull(touches);
    var entSession = EntitySessionHelper.GetEntitySession(session);
    var request = new DataRequest(entSession, sql, parameters, DbExecutionType.NonQuery, touches: touches);
    return (int)(await entSession.Database.RequestExecutor.ExecuteAsync(request))!;
  }

  /// <summary>Makes the request for a reading form and hands it to the database's executor.</summary>
  private static Task<object?> RunSqlRequestAsync(IEntitySession session, string sql,
      (string Name, object? Value)[] parameters, DbExecutionType executionType,
      IResultReader? resultReader = null, Type? readAs = null) {
    var entSession = EntitySessionHelper.GetEntitySession(session);
    return entSession.Database.RequestExecutor.ExecuteAsync(new DataRequest(entSession, sql, parameters, executionType, resultReader, readAs));
  }
}
