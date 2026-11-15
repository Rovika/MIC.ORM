using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Reflection;

namespace Mic.Common;

/// <summary>Represents one selectable enum value together with its display text.</summary>
/// <param name="Value">The enum value.</param>
/// <param name="Text">The display text of the value.</param>
public record EnumOption(object Value, string Text);

/// <summary>Provides the selectable options of an enum type for enum-bound controls. This class cannot be
/// inherited.</summary>
/// <remarks>The display text is language-neutral: the <c>[Display(Name)]</c> attribute, then <c>[Description]</c>,
/// then the field name. An enum control overlays localized text from the application's text package where a value
/// has one.</remarks>
public static class EnumHelper {
  /// <summary>Gets the options of an enum type, in declaration order.</summary>
  /// <param name="enumType">The enum type.</param>
  /// <param name="skipZero"><see langword="true"/> to leave out the member whose value is zero, such as a flags
  /// enum's <c>None</c>; otherwise, <see langword="false"/>.</param>
  /// <returns>A list of the options, or an empty list when <paramref name="enumType"/> is not an enum.</returns>
  public static IReadOnlyList<EnumOption> For(Type enumType, bool skipZero = false) {
    var list = new List<EnumOption>();
    if (!enumType.IsEnum) return list;
    foreach (var value in Enum.GetValues(enumType)) {
      if (skipZero && Convert.ToInt64(value) == 0) continue;
      var name = value!.ToString()!;
      list.Add(new EnumOption(value, DisplayText(enumType.GetField(name), name)));
    }
    return list;
  }

  private static string DisplayText(FieldInfo? field, string fallback) {
    var display = field?.GetCustomAttribute<DisplayAttribute>()?.GetName();
    if (!string.IsNullOrEmpty(display)) return display;
    var desc = field?.GetCustomAttribute<DescriptionAttribute>()?.Description;
    return string.IsNullOrEmpty(desc) ? fallback : desc;
  }
}
