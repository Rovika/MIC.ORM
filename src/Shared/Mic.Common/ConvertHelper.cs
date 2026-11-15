using System.Globalization;

namespace Mic.Common;

/// <summary>Provides conversions between the value a store hands back and the type of the member it belongs to.
///</summary>
public static class ConvertHelper {
  /// <summary>Converts a Guid to the 16 bytes a binary column holds, in RFC byte order.</summary>
  /// <param name="value">The Guid to convert.</param>
  /// <returns>An array that contains the 16 bytes of the Guid, in the order its text form writes them.</returns>
  public static byte[] GuidToBytes(Guid value) => value.ToByteArray(bigEndian: true);

  /// <summary>Converts a value as it came out of a store to the specified target type.</summary>
  /// <param name="value">The value from the store.</param>
  /// <param name="targetType">The type to convert to; a nullable type converts to its underlying type.</param>
  /// <returns>The converted value, or <see langword="null"/> when <paramref name="value"/> is <see langword="null"/>
  /// or <c>DBNull</c>.</returns>
  /// <remarks>A Guid converts from its text or its 16 bytes, a <c>DateOnly</c> or <c>TimeOnly</c> from a
  /// <c>DateTime</c>, a <c>TimeSpan</c> or text, an enum from its underlying integer, and anything else through
  /// <see cref="Convert.ChangeType(object, Type, IFormatProvider)"/>. A value already of the target type is returned
  /// as is.</remarks>
  public static object? ConvertValue(object? value, Type targetType) {
    if (value == null || value == DBNull.Value)
      return null;

    var wanted = Nullable.GetUnderlyingType(targetType) ?? targetType;
    if (wanted.IsInstanceOfType(value))
      return value;

    if (wanted == typeof(Guid))
      return value switch {
        string text => Guid.Parse(text),
        // The same RFC order GuidToBytes writes.
        byte[] { Length: 16 } bytes => new Guid(bytes, bigEndian: true),
        _ => value,
      };

    // No server has a DateOnly or a TimeOnly: one comes back as a DateTime, or as text on SQLite, which has no date
    // type. Convert.ChangeType makes neither.
    if (wanted == typeof(DateOnly))
      return value switch {
        DateTime dt => DateOnly.FromDateTime(dt),
        string text => DateOnly.Parse(text, CultureInfo.InvariantCulture),
        _ => value,
      };

    if (wanted == typeof(TimeOnly))
      return value switch {
        DateTime dt => TimeOnly.FromDateTime(dt),
        TimeSpan span => TimeOnly.FromTimeSpan(span),
        string text => TimeOnly.Parse(text, CultureInfo.InvariantCulture),
        _ => value,
      };

    // An enum arrives as its underlying integer; Convert.ChangeType will not make one.
    if (wanted.IsEnum)
      return Enum.ToObject(wanted, value);

    // A geographic point arrives as the well-known binary the statement asked the server for.
    if (wanted == typeof(GeoPoint))
      return value is byte[] wkb ? GeoPoint.FromWkb(wkb) : value;

    return Convert.ChangeType(value, wanted, CultureInfo.InvariantCulture);
  }
}
