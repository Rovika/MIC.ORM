namespace Mic.Orm.Db;

/// <summary>Represents rows to write into one table over a server's bulk-load channel.</summary>
/// <param name="TableRef">The table as a statement names it.</param>
/// <param name="ColumnNames">The names of the columns written, unquoted, in the order of a row's values.</param>
/// <param name="Rows">The rows, each holding one value for every column, adapted as a parameter value is.</param>
public sealed record BulkInsertInfo(string TableRef, IReadOnlyList<string> ColumnNames, IReadOnlyList<object?[]> Rows) {
  /// <summary>Finds the type of a column's values, taken from the first row that has one.</summary>
  /// <param name="column">The position of the column.</param>
  /// <returns>The type, or <see langword="null"/> where every row holds a null there.</returns>
  public Type? FindValueType(int column) {
    foreach (var row in Rows)
      if (row[column] is { } value)
        return value.GetType();
    return null;
  }

  /// <summary>Gets a SQL comment that says what is written, for a log and a list of commands: a bulk write has no
  /// statement of its own.</summary>
  /// <returns>The comment.</returns>
  public string Describe() =>
    $"-- bulk insert into {TableRef} ({string.Join(", ", ColumnNames)}): {Rows.Count} rows";
}
