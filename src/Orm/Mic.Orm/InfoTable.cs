using System.Data.Common;
using System.Globalization;

namespace Mic.Orm;

/// <summary>Represents the result of a catalog query, as rows of loosely typed values addressed by column name.
/// </summary>
/// <remarks>Column names are compared ignoring case. A column a provider's catalog query does not select is absent,
/// and a row reads it as <see langword="null"/>.</remarks>
public sealed class InfoTable {
  readonly Dictionary<string, int> _columnIndex = new(StringComparer.OrdinalIgnoreCase);
  readonly List<InfoRow> _rows = new();

  public InfoTable(IReadOnlyList<string> columns) {
    Columns = columns;
    for (var i = 0; i < columns.Count; i++)
      _columnIndex[columns[i]] = i;
  }

  public IReadOnlyList<string> Columns { get; }
  public IReadOnlyList<InfoRow> Rows => _rows;

  public void AddRow(object?[] values) => _rows.Add(new InfoRow(this, values));

  /// <summary>Gets the zero-based index of a column.</summary>
  /// <param name="column">The name of the column.</param>
  /// <returns>The zero-based index of the column, or -1 if the table has no such column.</returns>
  public int IndexOf(string column) => _columnIndex.TryGetValue(column, out var i) ? i : -1;

  public static async Task<InfoTable> ReadAsync(DbDataReader reader, CancellationToken cancellation = default,
      RunMode runMode = RunMode.Async) {
    var columns = new string[reader.FieldCount];
    for (var i = 0; i < reader.FieldCount; i++)
      columns[i] = reader.GetName(i);

    var table = new InfoTable(columns);
    while (runMode == RunMode.Sync ? reader.Read() : await reader.ReadAsync(cancellation)) {
      var values = new object?[reader.FieldCount];
      reader.GetValues(values!);
      for (var i = 0; i < values.Length; i++)
        if (values[i] == DBNull.Value)
          values[i] = null;
      table.AddRow(values);
    }
    return table;
  }

  public override string ToString() => $"{_rows.Count} rows [{string.Join(", ", Columns)}]";
}

public sealed class InfoRow {
  readonly InfoTable _table;
  readonly object?[] _values;

  internal InfoRow(InfoTable table, object?[] values) {
    _table = table;
    _values = values;
  }

  public object? this[string column] {
    get {
      var i = _table.IndexOf(column);
      return i < 0 ? null : _values[i];
    }
  }

  public string? GetString(string column) => this[column]?.ToString();

  public int GetInt(string column, int ifMissing = 0) {
    var value = this[column];
    return value == null ? ifMissing : Convert.ToInt32(value, CultureInfo.InvariantCulture);
  }

  public long GetLong(string column, long ifMissing = 0) {
    var value = this[column];
    return value == null ? ifMissing : Convert.ToInt64(value, CultureInfo.InvariantCulture);
  }

  /// <summary>Gets a column's value as a 64-bit integer, or <see langword="null"/> if the column is absent or null.
  /// </summary>
  public long? GetNullableLong(string column) =>
    this[column] is { } value ? Convert.ToInt64(value, CultureInfo.InvariantCulture) : null;

  /// <summary>Gets a column's value as a date and time.</summary>
  /// <param name="column">The name of the column.</param>
  /// <returns>The value, read from a date value or parsed from text using the invariant culture and the round-trip
  /// format; <see langword="null"/> if the column is absent, empty or not parsable.</returns>
  /// <remarks>Text is accepted because a server without a date type, such as SQLite, stores a date as a string.
  /// </remarks>
  public DateTime? GetDateTime(string column) => this[column] switch {
    DateTime value => value,
    string text when DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind,
      out var parsed) => parsed,
    _ => null,
  };

  /// <summary>Gets a column's value as a Boolean, in any of the spellings a vendor's catalog uses.</summary>
  /// <param name="column">The name of the column.</param>
  /// <param name="ifMissing">The value to return when the column is absent or <see langword="null"/>.</param>
  /// <returns><see langword="true"/> if the value is a true Boolean, the text <c>YES</c>, <c>true</c>, <c>1</c>,
  /// <c>T</c> or <c>Y</c> in any case, or a non-zero number; <paramref name="ifMissing"/> if the column is absent or
  /// <see langword="null"/>; otherwise, <see langword="false"/>.</returns>
  public bool GetBool(string column, bool ifMissing = false) {
    var value = this[column];
    return value switch {
      null => ifMissing,
      bool b => b,
      string s => s.Equals("YES", StringComparison.OrdinalIgnoreCase) || s.Equals("true", StringComparison.OrdinalIgnoreCase)
                  || s == "1" || s.Equals("t", StringComparison.OrdinalIgnoreCase)
                  || s.Equals("y", StringComparison.OrdinalIgnoreCase),
      _ => Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0,
    };
  }

  public override string ToString() => string.Join(" | ", _values);
}
