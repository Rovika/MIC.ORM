using System.Text;

namespace Mic.Common;

/// <summary>Provides small string utilities shared across the framework, written as character scans.</summary>
public static class StringHelper {
  /// <summary>Converts an identifier into a sentence-case caption: the last dotted segment, split at camel-case
  /// boundaries and underscores, with only the first letter capitalized.</summary>
  /// <param name="raw">The identifier.</param>
  /// <returns>The caption; for example <c>"BirthDate"</c> and <c>"birth_date"</c> become <c>"Birth date"</c>, and
  /// <c>"Address.City"</c> becomes <c>"City"</c>. For a <see langword="null"/> or blank identifier, <c>"Field"</c>.
  /// </returns>
  public static string HumanizeIdentifier(string? raw) {
    if (string.IsNullOrWhiteSpace(raw)) return "Field";

    var last = raw.Split('.')[^1];

    var sb = new StringBuilder(last.Length + 4);
    for (int i = 0; i < last.Length; i++) {
      char c = last[i];
      if (c == '_') { sb.Append(' '); continue; }
      // A camel-case boundary: an upper-case letter after a lower-case letter or a digit.
      if (i > 0 && char.IsUpper(c) && (char.IsLower(last[i - 1]) || char.IsDigit(last[i - 1])))
        sb.Append(' ');
      sb.Append(c);
    }

    var spaced = sb.ToString().Trim();
    if (spaced.Length == 0) return "Field";
    return char.ToUpper(spaced[0]) + spaced[1..].ToLowerInvariant();
  }

  /// <summary>Forms the English plural of a singular identifier by simple rules, with no irregular forms.</summary>
  /// <param name="singular">The singular identifier.</param>
  /// <returns>The plural; an empty string for a <see langword="null"/> or empty identifier.</returns>
  /// <remarks>A word ending in s, x, z, ch or sh takes "es"; a consonant followed by y becomes "ies"; anything else
  /// takes "s", so <c>"Person"</c> becomes <c>"Persons"</c>. The stem keeps its case; the suffix is lower-case.
  /// </remarks>
  public static string Pluralize(string? singular) {
    if (string.IsNullOrEmpty(singular)) return singular ?? "";
    var lower = singular.ToLowerInvariant();
    if (lower.EndsWith("s") || lower.EndsWith("x") || lower.EndsWith("z") || lower.EndsWith("ch") || lower.EndsWith("sh"))
      return singular + "es";
    if (lower.Length >= 2 && lower[^1] == 'y' && !IsVowel(lower[^2]))
      return singular[..^1] + "ies";
    return singular + "s";
  }

  private static bool IsVowel(char c) => c is 'a' or 'e' or 'i' or 'o' or 'u';

  // --- caret / word scanning ---------------------------------------------------------------------------

  /// <summary>Counts the whitespace characters the text opens with.</summary>
  /// <param name="s">The text.</param>
  /// <returns>The number of leading whitespace characters, which is the index of the first non-space character.
  /// </returns>
  public static int LeadingSpace(string? s) {
    var text = s ?? "";
    var i = 0;
    while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
    return i;
  }

  /// <summary>Finds the start of the last whitespace-separated word.</summary>
  /// <param name="s">The text.</param>
  /// <returns>The zero-based index of the word's first character, or the length of the text when it ends in
  /// whitespace, which a completion list reads as an empty partial word.</returns>
  public static int LastWordStart(string? s) {
    var text = s ?? "";
    var i = text.Length;
    while (i > 0 && !char.IsWhiteSpace(text[i - 1])) i--;
    return i;
  }
}
