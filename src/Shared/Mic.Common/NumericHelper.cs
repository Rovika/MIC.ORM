using System.Globalization;

namespace Mic.Common;

/// <summary>Provides culture-aware formatting and parsing of numeric values, including the nullable types, for the
/// numeric entry controls.</summary>
public static class NumericHelper {
  /// <summary>Formats a value for display under a culture.</summary>
  /// <typeparam name="TValue">The type of the value.</typeparam>
  /// <param name="value">The value to format.</param>
  /// <param name="format">The .NET format string, or <see langword="null"/> or empty for the default format.</param>
  /// <param name="culture">The culture to format under, or <see langword="null"/> for the current culture.</param>
  /// <returns>The formatted text, or an empty string when <paramref name="value"/> is <see langword="null"/>.</returns>
  public static string Format<TValue>(TValue? value, string? format, CultureInfo? culture) {
    if (value is null)
      return "";
    if (value is IFormattable f)
      return f.ToString(string.IsNullOrEmpty(format) ? null : format, culture);
    return value.ToString() ?? "";
  }

  /// <summary>Parses typed text back to a numeric value under a culture.</summary>
  /// <typeparam name="TValue">The numeric type, or its nullable form.</typeparam>
  /// <param name="raw">The text to parse.</param>
  /// <param name="culture">The culture whose separators the text uses, or <see langword="null"/> for the current
  /// culture.</param>
  /// <param name="value">When this method returns, contains the parsed value, or the default of
  /// <typeparamref name="TValue"/> when the parse failed or the text was empty.</param>
  /// <returns><see langword="true"/> if the text parsed, or was empty and <typeparamref name="TValue"/> is nullable;
  /// <see langword="false"/> if the text did not parse, is out of range, or was empty for a non-nullable type.
  /// </returns>
  public static bool TryParse<TValue>(string? raw, CultureInfo? culture, out TValue? value) {
    value = default;
    var cultureInfo = culture ?? CultureInfo.CurrentCulture;
    var underlying = Nullable.GetUnderlyingType(typeof(TValue)) ?? typeof(TValue);
    var nullable = Nullable.GetUnderlyingType(typeof(TValue)) != null || !typeof(TValue).IsValueType;
    if (string.IsNullOrWhiteSpace(raw))
      return nullable;   // empty clears a nullable type to null and is rejected for a non-nullable one
    try {
      // Integer types reject a fraction: NumberStyles.Integer allows a sign and thousands separators only.
      if (underlying.IsIntegerType()) {
        if (!long.TryParse(raw, NumberStyles.Integer | NumberStyles.AllowThousands, cultureInfo, out var whole))
          return false;
        value = (TValue)Convert.ChangeType(whole, underlying, cultureInfo);
        return true;
      }
      if (!decimal.TryParse(raw, NumberStyles.Number, cultureInfo, out var dec))
        return false;
      value = (TValue)Convert.ChangeType(dec, underlying, cultureInfo);
      return true;
    }
    catch {
      return false;
    }
  }
}
