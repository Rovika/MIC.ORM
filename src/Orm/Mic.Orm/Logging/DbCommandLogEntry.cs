using System.Data.Common;
using System.Text;
using Mic.Orm.Db;

namespace Mic.Orm.Logging;

/// <summary>Represents one statement that ran: its SQL, the parameters it bound, how many rows it touched and how
/// long it took.</summary>
/// <remarks>The entry copies what it needs from the command at capture and does not hold the command; the producing
/// thread copies, and the background thread formats. At most <see cref="MaxParameters"/> parameter values are kept.
/// </remarks>
public class DbCommandLogEntry : LogEntry {
  /// <summary>Specifies how many parameter values are kept; the rest are counted and dropped.</summary>
  public const int MaxParameters = 10;

  /// <summary>Specifies the longest a single value is rendered as.</summary>
  const int ValueLimit = 50;

  readonly (string Name, object? Value)[] _parameters;

  public DbCommandLogEntry(LogSource source, DateTime time, DbCommand dbCommand, DbExecutionType executionType,
      int rowCount, double timeMs) : base(source, time) {
    Sql = dbCommand.CommandText;
    ExecutionType = executionType;
    RowCount = rowCount;
    TimeMs = timeMs;

    var parameters = dbCommand.Parameters;
    ParameterCount = parameters.Count;
    var kept = Math.Min(ParameterCount, MaxParameters);
    _parameters = kept == 0 ? [] : new (string, object?)[kept];
    for (var i = 0; i < kept; i++) {
      var parameter = parameters[i];
      _parameters[i] = (parameter.ParameterName, Capture(parameter.Value));
    }
  }

  public string Sql { get; }
  public DbExecutionType ExecutionType { get; }
  public int RowCount { get; }
  public double TimeMs { get; }

  /// <summary>Gets or initializes the id of the save the statement ran in, or <see langword="null"/> for a read
  /// or a schema statement.</summary>
  public Guid? TransactionId { get; init; }

  /// <summary>Gets how many parameters the statement bound, however few were kept.</summary>
  public int ParameterCount { get; }

  /// <summary>Specifies the longest binary value rendered as hex rather than reduced to its length: the sixteen bytes
  /// of a Guid.</summary>
  const int HexLimit = 16;

  /// <summary>Captures a parameter value: the reference itself, except a byte array, which is rendered to text at
  /// once so the array is not held until flush.</summary>
  private static object? Capture(object? value) => value switch {
    byte[] bytes => new RenderedText(
      bytes.Length <= HexLimit ? $"0x{Convert.ToHexString(bytes)}" : $"byte[{bytes.Length}]"),
    _ => value,
  };

  /// <summary>Represents a value already turned into its final text at capture, printed verbatim.</summary>
  readonly record struct RenderedText(string Text);

  public override void WriteText(StringBuilder text) {
    // A blank line before each statement, and the SQL at column zero so it can be pasted and run; everything else
    // goes on the `--` lines under it.
    text.AppendLine();
    text.AppendLine(Sql);

    if (_parameters.Length > 0) {
      text.Append("    -- Parameters: ");
      for (var i = 0; i < _parameters.Length; i++) {
        if (i > 0)
          text.Append(", ");
        text.Append(_parameters[i].Name).Append('=').Append(Render(_parameters[i].Value));
      }
      if (ParameterCount > _parameters.Length)
        text.Append(", … (").Append(_parameters.Length).Append(" of ").Append(ParameterCount).Append(')');
      text.AppendLine();
    }

    text.Append("    -- ").Append(TimeMs.ToString("F1")).Append(" ms");
    if (RowCount >= 0)
      text.Append(", ").Append(RowCount).Append(" row(s)");
    text.Append(", ").Append(Time.ToString("HH:mm:ss.fff"));
    if (TransactionId is { } transactionId)
      text.Append(", tx ").Append(transactionId.ToString("N"));
    text.AppendLine();
  }

  /// <summary>Renders a value without its type; a string keeps its quotes.</summary>
  private static string Render(object? value) => value switch {
    null or DBNull => "NULL",
    RenderedText already => already.Text,
    string s => $"'{Trim(s)}'",
    _ => Trim(value.ToString() ?? ""),
  };

  private static string Trim(string value) =>
    value.Length <= ValueLimit ? value : string.Concat(value.AsSpan(0, ValueLimit), "…");
}
