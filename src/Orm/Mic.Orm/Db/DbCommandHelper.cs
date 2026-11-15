using System.Data.Common;

namespace Mic.Orm.Db;

/// <summary>Renders a driver command as text for the message of a failure.</summary>
public static class DbCommandHelper {
  /// <summary>Specifies the longest a single parameter value is rendered as, in characters.</summary>
  public const int ValueLimit = 50;

  /// <summary>Renders a command's statement and parameter values as text, each value trimmed to
  /// <see cref="ValueLimit"/> characters and followed by its type.</summary>
  /// <param name="dbCommand">The command to render.</param>
  /// <returns>The statement, followed by one line per parameter.</returns>
  public static string Describe(DbCommand dbCommand) {
    ArgumentNullException.ThrowIfNull(dbCommand);
    var text = new System.Text.StringBuilder(dbCommand.CommandText);
    foreach (DbParameter parameter in dbCommand.Parameters)
      text.Append(Environment.NewLine).Append("  ").Append(parameter.ParameterName)
        .Append(" = ").Append(Describe(parameter.Value));
    return text.ToString();
  }

  private static string Describe(object? value) => value switch {
    null or DBNull => "NULL",
    // ToString on an array is its type name; the length is what a reader can use.
    byte[] bytes => $"byte[{bytes.Length}]",
    string s => $"'{Trim(s)}' (String)",
    _ => $"{Trim(value.ToString() ?? "")} ({value.GetType().Name})",
  };

  private static string Trim(string value) =>
    value.Length <= ValueLimit ? value : string.Concat(value.AsSpan(0, ValueLimit), "…");
}
