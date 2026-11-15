using System.Globalization;
using System.Text;

namespace Mic.Common;

/// <summary>Provides the date-pattern helpers shared by the masked date entry controls. This class cannot be
/// inherited.</summary>
public static class DateHelper {
  /// <summary>Gets the culture's short-date pattern normalized to fixed-width slots for an input mask.</summary>
  /// <param name="culture">The culture whose short-date pattern is used.</param>
  /// <returns>The pattern with day and month tokens padded to two characters, the year at its own width and the
  /// separators unchanged; for example <c>"M/d/yyyy"</c> becomes <c>"MM/dd/yyyy"</c>.</returns>
  public static string MaskPattern(CultureInfo culture) => NormalizePattern(culture.DateTimeFormat.ShortDatePattern);

  public static string NormalizePattern(string pattern) {
    var sb = new StringBuilder();
    for (var i = 0; i < pattern.Length;) {
      var ch = pattern[i];
      if (ch is 'd' or 'M' or 'y') {
        var j = i; while (j < pattern.Length && pattern[j] == ch) j++;
        sb.Append(ch == 'y' ? new string('y', j - i) : new string(ch, 2));   // day/month pad to 2; year keeps its width
        i = j;
      }
      else { sb.Append(ch); i++; }
    }
    return sb.ToString();
  }
}
