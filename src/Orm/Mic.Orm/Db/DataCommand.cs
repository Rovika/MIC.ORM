using System.Data.Common;
using Mic.Orm.Db.Readers;
using Mic.Orm.Runtime;

namespace Mic.Orm.Db;

/// <summary>Represents one statement, built and ready to run, and what running it produced.</summary>
public sealed class DataCommand {
  /// <summary>Initializes a new instance of the <see cref="DataCommand"/> class.</summary>
  public DataCommand(DbCommand dbCommand, DbExecutionType executionType, string? description = null,
      IResultReader? resultReader = null) {
    DbCommand = dbCommand;
    ExecutionType = executionType;
    Description = description;
    ResultReader = resultReader;
  }

  /// <summary>Gets the driver command.</summary>
  public DbCommand DbCommand { get; }

  /// <summary>Gets how the command is executed: for its rows, for one value, or for its row count.</summary>
  public DbExecutionType ExecutionType { get; }

  /// <summary>Gets the short name a log line uses for this command.</summary>
  public string? Description { get; }

  /// <summary>Gets or sets the result reader for the rows of a <see cref="DbExecutionType.Reader"/> command;
  /// <see langword="null"/> buffers them into an <see cref="InfoTable"/>.</summary>
  public IResultReader? ResultReader { get; set; }

  /// <summary>Gets or sets the record this statement writes, where it writes one. A generated id read back as the
  /// command's result is put on it.</summary>
  public Runtime.EntityRecord? Record { get; set; }

  /// <summary>Gets or sets the records this statement writes, where it writes many, so that a failure can name the
  /// row the server refused.</summary>
  public IReadOnlyList<Runtime.EntityRecord>? Records { get; set; }

  /// <summary>Gets or sets every record a batch's statements wrote, in order, where the batch carried generated ids:
  /// the keys are put back on the records whose <see cref="Runtime.EntityRecord.GeneratedIdSlot"/> says where the batch
  /// left them. <see langword="null"/> for a command that carried none.</summary>
  public IReadOnlyList<Runtime.EntityRecord>? BatchRecords { get; set; }

  /// <summary>Gets or sets the number of rows the command must report affected, for an update or a delete that
  /// matches on the row's version; any other count is a concurrent update, which the executor refuses. -1 where the
  /// count says nothing.</summary>
  public int ExpectedRowCount { get; set; } = -1;

  /// <summary>Gets or sets a value that indicates whether the command's text begins and commits its own
  /// transaction, as a joined batch does. The executor then begins none of its own and rolls the batch back
  /// explicitly on failure.</summary>
  public bool CarriesItsOwnTransaction { get; set; }

  /// <summary>Gets the rows the command writes over the server's bulk channel, or <see langword="null"/> for a
  /// command that runs its statement.</summary>
  /// <remarks>Such a command's text is a comment that says what is written, for the log and the session's last
  /// commands; it is never sent.</remarks>
  public BulkInsertInfo? BulkInsert { get; init; }

  /// <summary>Gets or sets how the command is run and its rows are read; the executor sets it from the session.
  /// </summary>
  public RunMode RunMode { get; set; }

  /// <summary>Asynchronously advances the reader to the next row, under the command's <see cref="RunMode"/>.
  /// </summary>
  /// <param name="reader">The open reader of this command.</param>
  /// <param name="cancellation">A token to cancel the operation.</param>
  /// <returns>A task whose result is <see langword="true"/> if there is a row; otherwise, <see langword="false"/>.
  /// Under <see cref="RunMode.Sync"/> the task is complete when returned.</returns>
  public ValueTask<bool> ReadRowAsync(DbDataReader reader, CancellationToken cancellation) =>
    RunMode == RunMode.Sync ? new(reader.Read()) : new(reader.ReadAsync(cancellation));

  // ----- results, filled by the executor

  /// <summary>Gets or sets what the command produced: a row count, a scalar, or what the result reader returned. Never a live
  /// reader.</summary>
  public object? Result { get; set; }

  /// <summary>Gets or sets the number of rows the command affected; -1 where it was not a write.</summary>
  public int RowCount { get; set; } = -1;

  /// <summary>Gets or sets the duration of the command, in milliseconds.</summary>
  public double TimeMs { get; set; }

  public override string ToString() => Description ?? DbCommand.CommandText;
}
