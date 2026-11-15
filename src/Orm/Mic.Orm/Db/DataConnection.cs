using System.Data;
using System.Data.Common;

namespace Mic.Orm.Db;

/// <summary>Represents a <see cref="DbConnection"/> together with its transaction, its lifetime and the reader open
/// on it.</summary>
public class DataConnection : IDisposable, IAsyncDisposable {
  public DataConnection(Database database, DbConnection dbConnection, DbConnectionLifetime lifetime) {
    Database = database;
    DbConnection = dbConnection;
    Lifetime = lifetime;
  }

  public Database Database { get; }
  public DbConnection DbConnection { get; }
  public DbTransaction? DbTransaction { get; private set; }

  public DbConnectionLifetime Lifetime { get; set; }
  public DbConnectionFlags Flags { get; set; }

  /// <summary>Gets or sets the reader open on this connection, which the next command closes before it runs.</summary>
  /// <remarks>Npgsql has no MARS; a reader left open makes the next command on the same connection fail.</remarks>
  public DbDataReader? ActiveReader { get; set; }

  public bool IsOpen => DbConnection.State == ConnectionState.Open;

  public async Task OpenAsync(RunMode runMode, CancellationToken cancellation = default) {
    if (IsOpen)
      return;
    await CloseActiveReaderAsync(runMode);
    try {
      if (runMode == RunMode.Sync)
        DbConnection.Open();
      else
        await DbConnection.OpenAsync(cancellation);
    } catch (DbException ex) {
      var failure = Database.Provider.Fail(
        $"Failed to open a connection to '{Database.Settings.DatabaseAlias}'.", ex,
        Database.Settings.DatabaseAlias);
      // Whatever the server said, a failure to open is reported as a connection failure.
      failure.SubType = DataAccessException.SubTypeConnectionFailed;
      throw failure;
    }
  }

  public async Task CloseAsync(RunMode runMode) {
    if (DbTransaction != null)
      throw new InvalidOperationException(
        "Cannot close the connection — it has an active transaction. Commit or abort it first.");
    await CloseActiveReaderAsync(runMode);
    if (DbConnection.State == ConnectionState.Closed)
      return;
    if (runMode == RunMode.Sync)
      DbConnection.Close();
    else
      await DbConnection.CloseAsync();
  }

  public async Task CloseActiveReaderAsync(RunMode runMode) {
    if (ActiveReader == null)
      return;
    if (runMode == RunMode.Sync)
      ActiveReader.Dispose();
    else
      await ActiveReader.DisposeAsync();
    ActiveReader = null;
  }

  // ----- transactions -----------------------------------------------------------------------------------------------

  /// <summary>Asynchronously begins a transaction on this connection, opening it first.</summary>
  /// <param name="runMode">One of the enumeration values that specifies how the database calls are made.</param>
  /// <param name="commitOnSave">
  /// <see langword="true"/> to commit the transaction on the session's next save, as for one begun by a locked read
  /// rather than by the caller; otherwise, <see langword="false"/>.</param>
  /// <param name="isolationLevel">The isolation level of the transaction.</param>
  /// <param name="cancellation">A token to cancel the operation.</param>
  /// <remarks>The connection stays open while the transaction lives, whatever its lifetime says.</remarks>
  public async Task BeginTransactionAsync(RunMode runMode, bool commitOnSave,
      IsolationLevel isolationLevel = IsolationLevel.Unspecified, CancellationToken cancellation = default) {
    if (DbTransaction != null)
      throw new InvalidOperationException("The connection already has a transaction; commit or abort it first.");
    await OpenAsync(runMode, cancellation);
    DbTransaction = runMode == RunMode.Sync
      ? DbConnection.BeginTransaction(isolationLevel)
      : await DbConnection.BeginTransactionAsync(isolationLevel, cancellation);
    if (commitOnSave)
      Flags |= DbConnectionFlags.CommitOnSave;
  }

  public async Task CommitAsync(RunMode runMode) {
    if (DbTransaction == null)
      return;
    if (runMode == RunMode.Sync) {
      DbTransaction.Commit();
      DbTransaction.Dispose();
    } else {
      await DbTransaction.CommitAsync();
      await DbTransaction.DisposeAsync();
    }
    DbTransaction = null;
    Flags &= ~DbConnectionFlags.CommitOnSave;
  }

  /// <summary>Asynchronously rolls back the transaction, without throwing if the rollback fails.</summary>
  /// <remarks>This runs on the failure path, where a second exception would replace the one worth reading.</remarks>
  public async Task AbortAsync(RunMode runMode) {
    if (DbTransaction == null)
      return;
    var sync = runMode == RunMode.Sync;
    try {
      if (IsOpen && sync)
        DbTransaction.Rollback();
      else if (IsOpen)
        await DbTransaction.RollbackAsync();
    } catch (DbException) {
      // The connection is already broken, which is why we are here.
    } finally {
      if (sync)
        DbTransaction.Dispose();
      else
        await DbTransaction.DisposeAsync();
      DbTransaction = null;
      Flags &= ~DbConnectionFlags.CommitOnSave;
    }
  }

  // ----- disposal ---------------------------------------------------------------------------------------------------

  public async ValueTask DisposeAsync() {
    await AbortAsync(RunMode.Async);
    await CloseActiveReaderAsync(RunMode.Async);
    await DbConnection.DisposeAsync();
    GC.SuppressFinalize(this);
  }

  public void Dispose() {
    // Under the synchronous mode both tasks are complete when returned.
    AbortAsync(RunMode.Sync).GetAwaiter().GetResult();
    CloseActiveReaderAsync(RunMode.Sync).GetAwaiter().GetResult();
    DbConnection.Dispose();
    GC.SuppressFinalize(this);
  }

  public override string ToString() =>
    $"{Database.Settings.DatabaseAlias}: {DbConnection.State}" + (DbTransaction == null ? "" : ", in transaction");
}
