using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Mic.Orm.Db.Provider;

namespace Mic.Orm.Db.Sql;

/// <summary>Writes the rows of a JSON parameter, a JSON array of objects with one property per column, in the form
/// the provider's <see cref="SqlDialect.JsonRowsSource"/> reads back typed.</summary>
public static class JsonRowsHelper {
  /// <summary>Serializes rows as JSON text, one object per row with the columns' names as its properties.</summary>
  /// <param name="provider">The provider of the server the rows go to.</param>
  /// <param name="columns">The columns of a row, in order.</param>
  /// <param name="rows">The rows, each an array with one value per column.</param>
  /// <returns>The JSON text.</returns>
  /// <remarks>A Guid is written as hex where the provider stores Guids as bytes and as text otherwise; a date is ISO
  /// 8601 without an offset; an enum is its underlying number.</remarks>
  public static string Serialize(DbProvider provider, IReadOnlyList<JsonRowsColumn> columns, IEnumerable<object?[]> rows) {
    ArgumentNullException.ThrowIfNull(provider);
    ArgumentNullException.ThrowIfNull(columns);
    ArgumentNullException.ThrowIfNull(rows);
    var buffer = new ArrayBufferWriter<byte>();
    using (var writer = new Utf8JsonWriter(buffer)) {
      writer.WriteStartArray();
      foreach (var row in rows) {
        if (row.Length != columns.Count)
          throw new ArgumentException($"A row has {row.Length} value(s) and {columns.Count} column(s) are declared.", nameof(rows));
        writer.WriteStartObject();
        for (var i = 0; i < columns.Count; i++) {
          writer.WritePropertyName(columns[i].Name);
          WriteValue(writer, provider, row[i]);
        }
        writer.WriteEndObject();
      }
      writer.WriteEndArray();
    }
    return Encoding.UTF8.GetString(buffer.WrittenSpan);
  }

  /// <summary>Writes one row as a JSON object, each value under its column's name, as <see cref="Serialize"/> writes
  /// each element of its array.</summary>
  /// <param name="provider">The provider, which decides how a Guid travels.</param>
  /// <param name="columns">The columns, in the row's order.</param>
  /// <param name="row">The row's values.</param>
  /// <returns>The JSON text of the object.</returns>
  public static string SerializeRow(DbProvider provider, IReadOnlyList<JsonRowsColumn> columns, object?[] row) {
    ArgumentNullException.ThrowIfNull(provider);
    ArgumentNullException.ThrowIfNull(columns);
    ArgumentNullException.ThrowIfNull(row);
    var buffer = new ArrayBufferWriter<byte>();
    using (var writer = new Utf8JsonWriter(buffer)) {
      writer.WriteStartObject();
      for (var i = 0; i < columns.Count; i++) {
        writer.WritePropertyName(columns[i].Name);
        WriteValue(writer, provider, row[i]);
      }
      writer.WriteEndObject();
    }
    return Encoding.UTF8.GetString(buffer.WrittenSpan);
  }

  /// <summary>Gets the text a Guid travels as in the rows of a parameter: the hex of its bytes on a server that stores
  /// a Guid as bytes, which the dialect's spelling turns back; its usual form elsewhere.</summary>
  public static string GetGuidText(DbProvider provider, Guid guid) {
    ArgumentNullException.ThrowIfNull(provider);
    return provider.AdaptParameterValue(guid) is byte[] bytes ? Convert.ToHexString(bytes) : guid.ToString("D");
  }

  public static IEnumerable<object?[]> GetRows(object? value, Func<object, object?[]?>? readRow = null) {
    switch (value) {
      case null:
        return [];
      case IEnumerable<object?[]> rows:
        return rows;
      case string:
        throw new ArgumentException("The rows of a parameter are a list of rows, not a string.", nameof(value));
      case System.Collections.IEnumerable items:
        return EnumerateRows(items, readRow);
      default:
        throw new ArgumentException($"The rows of a parameter are a list of rows, not a {value.GetType().Name}.", nameof(value));
    }
  }

  private static IEnumerable<object?[]> EnumerateRows(System.Collections.IEnumerable items, Func<object, object?[]?>? readRow) {
    foreach (var item in items)
      yield return item switch {
        null => [null],
        object?[] row => row,
        System.Runtime.CompilerServices.ITuple tuple => GetItems(tuple),
        IReadOnlyList<object?> list => list.ToArray(),
        _ => readRow?.Invoke(item) ?? [item],
      };
  }

  private static object?[] GetItems(System.Runtime.CompilerServices.ITuple tuple) {
    var items = new object?[tuple.Length];
    for (var i = 0; i < items.Length; i++)
      items[i] = tuple[i];
    return items;
  }

  private static void WriteValue(Utf8JsonWriter writer, DbProvider provider, object? value) {
    switch (value) {
      case null:
        writer.WriteNullValue();
        break;
      case Guid guid:
        // Bytes become hex for the spelling to turn back; a server with a Guid type takes the text.
        if (provider.AdaptParameterValue(guid) is byte[] guidBytes)
          writer.WriteStringValue(Convert.ToHexString(guidBytes));
        else
          writer.WriteStringValue(guid.ToString("D"));
        break;
      case byte[] bytes:
        writer.WriteStringValue(Convert.ToHexString(bytes));
        break;
      case string text:
        writer.WriteStringValue(text);
        break;
      case bool flag:
        writer.WriteBooleanValue(flag);
        break;
      case int number:
        writer.WriteNumberValue(number);
        break;
      case long number:
        writer.WriteNumberValue(number);
        break;
      case short number:
        writer.WriteNumberValue(number);
        break;
      case byte number:
        writer.WriteNumberValue(number);
        break;
      case decimal number:
        writer.WriteNumberValue(number);
        break;
      case double number:
        writer.WriteNumberValue(number);
        break;
      case float number:
        writer.WriteNumberValue(number);
        break;
      case DateTime dateTime:
        writer.WriteStringValue(dateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture));
        break;
      case DateOnly date:
        writer.WriteStringValue(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        break;
      case Enum member:
        writer.WriteNumberValue(Convert.ToInt64(member, CultureInfo.InvariantCulture));
        break;
      default:
        writer.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture));
        break;
    }
  }
}
