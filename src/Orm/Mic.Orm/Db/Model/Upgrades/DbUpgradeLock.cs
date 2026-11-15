using System.Diagnostics;
using Mic.Common;
using Mic.Orm.Db.Provider;

namespace Mic.Orm.Db.Model.Upgrades;

/// <summary>Represents the session lock one instance holds while it upgrades a database, so that other instances
/// starting at the same time wait rather than apply the same upgrade.</summary>
/// <remarks>The lock is held on a connection of its own and released when the lock is disposed or the connection
/// drops, so an instance that dies mid-upgrade blocks nobody.</remarks>
public sealed class DbUpgradeLock : IAsyncDisposable {
  DbUpgradeLock(Database database, DataConnection connection, long key) {
    Database = database;
    Connection = connection;
    Key = key;
  }

  public Database Database { get; }
  public DataConnection Connection { get; }

  /// <summary>Gets the lock's key, a deterministic hash of the application name, so two applications sharing a
  /// server never wait on each other.</summary>
  public long Key { get; }

  public static long GetLockKey(string appName) => unchecked((long)Fnv1aHash.Compute(appName));

  /// <summary>Asynchronously takes the upgrade lock of a database, polling until it is free or the wait runs out.
  /// </summary>
  /// <param name="database">The database to lock.</param>
  /// <param name="wait">The longest to wait for the lock.</param>
  /// <param name="cancellation">A token to cancel the operation.</param>
  /// <returns>A task that represents the asynchronous operation. The task result contains the lock, or
  /// <see langword="null"/> if another instance still holds it when the wait runs out or the server has no session
  /// locks.</returns>
  /// <exception cref="DataAccessException">The server refused the lock request itself, with
  /// <see cref="DataAccessException.SubTypeAccessDenied"/>: the user lacks the privilege the lock needs.</exception>
  public static async Task<DbUpgradeLock?> TryAcquireAsync(Database database, TimeSpan wait,
      CancellationToken cancellation = default) {
    if (!database.Provider.Supports(DbFeatures.SessionLocks))
      return null;

    var key = GetLockKey(database.DbModel.EntityModel?.Name ?? database.Settings.DatabaseAlias);
    var sql = string.Format(database.Provider.SqlDialect.TryAcquireSessionLock, key);
    var connection = database.CreateConnection(DbConnectionLifetime.Explicit, forSchema: true);
    try {
      await connection.OpenAsync(RunMode.Async, cancellation);
      var started = Stopwatch.GetTimestamp();
      while (true) {
        if (await TryOnceAsync(database, connection, sql, cancellation))
          return new DbUpgradeLock(database, connection, key);
        if (Stopwatch.GetElapsedTime(started) >= wait)
          break;
        await Task.Delay(PollInterval, cancellation);
      }
    } catch {
      await connection.DisposeAsync();
      throw;
    }
    await connection.DisposeAsync();
    return null;
  }

  static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

  private static async Task<bool> TryOnceAsync(Database database, DataConnection connection, string sql,
      CancellationToken cancellation) {
    using var dbCommand = database.Provider.CreateDbCommand();
    dbCommand.Connection = connection.DbConnection;
    dbCommand.CommandText = sql;
    object? result;
    try {
      result = await dbCommand.ExecuteScalarAsync(cancellation);
    } catch (System.Data.Common.DbException ex) {
      // The connection has just opened, so the request itself was refused: the user lacks the privilege it needs.
      var hint = database.Provider.SqlDialect.SessionLockGrantHint;
      var failure = database.Provider.Fail(
        $"The session lock request was refused: {ex.Message.Trim()}" + (hint.Length > 0 ? $" Needed: {hint}." : ""),
        ex, database.Settings.DatabaseAlias);
      failure.SubType = DataAccessException.SubTypeAccessDenied;
      throw failure;
    }
    // The server answers with a Boolean where it has one and a number where it does not.
    return result switch {
      bool taken => taken,
      null or DBNull => false,
      _ => Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture) != 0,
    };
  }

  public async ValueTask DisposeAsync() {
    var release = Database.Provider.SqlDialect.ReleaseSessionLock;
    try {
      if (release.Length > 0 && Connection.IsOpen) {
        using var dbCommand = Database.Provider.CreateDbCommand();
        dbCommand.Connection = Connection.DbConnection;
        dbCommand.CommandText = string.Format(release, Key);
        await dbCommand.ExecuteScalarAsync();
      }
    } catch (System.Data.Common.DbException) {
      // Closing the connection below releases the lock whether or not the release statement ran.
    }
    await Connection.DisposeAsync();
  }
}
