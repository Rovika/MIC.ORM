using Mic.Common;

namespace Mic.Orm.Db.Model.Upgrades;

/// <summary>Computes the hash of a generated definition, stored beside the object so a later run can tell whether
/// the definition changed without comparing text the server has rewritten.</summary>
/// <remarks>The hash is a deterministic FNV-1a hash, so it is the same on every machine and every run.</remarks>
public static class DefinitionHashHelper {
  /// <summary>Gets the hash of a definition as the marker stored in a comment, with <see cref="Prefix"/> in front.
  /// </summary>
  /// <param name="definition">The definition text.</param>
  /// <returns>The marker.</returns>
  public static string Of(string definition) => Prefix + Fnv1aHash.ComputeHex(definition);

  public const string Prefix = "mic:h=";

  public static ulong Compute(string text) => Fnv1aHash.Compute(text);

  /// <summary>Finds the marker in text that carries one, such as a comment or a definition the marker was written
  /// into.</summary>
  /// <param name="text">The text, or <see langword="null"/>.</param>
  /// <returns>The marker, with <see cref="Prefix"/> in front, or <see langword="null"/> if the text carries none.
  /// </returns>
  public static string? FindMarker(string? text) {
    if (text == null)
      return null;
    var start = text.IndexOf(Prefix, StringComparison.Ordinal);
    if (start < 0)
      return null;
    var end = start + Prefix.Length;
    while (end < text.Length && char.IsAsciiHexDigit(text[end]))
      end++;
    return end == start + Prefix.Length ? null : text[start..end];
  }
}
