using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Reflection;

namespace Mic.Common;

/// <summary>Provides typed accessors over a string-valued metadata dictionary, such as a UI element's properties.
///</summary>
public static class CommonExtensions {
  /// <summary>Gets the display name of an enum member from its <c>[Display(Name = ...)]</c> attribute.</summary>
  /// <param name="value">The enum value.</param>
  /// <returns>The display name, or the member's own name when it has no display attribute.</returns>
  public static string GetDisplayName(this Enum value) {
    var member = value.GetType().GetMember(value.ToString()).FirstOrDefault();
    return member?.GetCustomAttribute<DisplayAttribute>()?.GetName() ?? value.ToString();
  }

  public static string? GetString(this Dictionary<string, string> props, string key, string? fallback = null) =>
    props.TryGetValue(key, out var v) ? v : fallback;

  public static int GetInt(this Dictionary<string, string> props, string key, int fallback = 0) =>
    props.TryGetValue(key, out var v) &&
    int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : fallback;

  public static bool GetBool(this Dictionary<string, string> props, string key, bool fallback = false) =>
    props.TryGetValue(key, out var v) && bool.TryParse(v, out var b) ? b : fallback;

  public static T GetEnum<T>(this Dictionary<string, string> props, string key, T fallback) where T : struct, Enum =>
    props.TryGetValue(key, out var v) && Enum.TryParse<T>(v, ignoreCase: true, out var e) ? e : fallback;
}
