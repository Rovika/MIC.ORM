using Mic.Orm.Db;

namespace Mic.Orm;


/// <summary>Represents what a command was after it has run: its text, its description, how it ran, its row count
/// and its duration.</summary>
/// <remarks>A copy, taken while the driver command is alive and before it is disposed. It holds no records, so the
/// session's last commands pin nothing, and no parameter values; a failure renders those in its exception.</remarks>
public sealed record DataCommandSnapshot(
  string Sql, string? Description, DbExecutionType ExecutionType, int RowCount, double TimeMs) {
  public override string ToString() => $"{Description ?? Sql} — {RowCount} rows, {TimeMs:F1} ms";
}
