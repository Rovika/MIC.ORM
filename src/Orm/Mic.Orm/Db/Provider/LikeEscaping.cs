namespace Mic.Orm.Db.Provider;

/// <summary>Represents how a server wants a <c>LIKE</c> pattern escaped: the escape character, and the wildcard
/// characters it goes in front of.</summary>
/// <param name="Escape">The escape character, which the SQL declares and the value is escaped with.</param>
/// <param name="Wildcards">The characters the server reads as wildcards.</param>
/// <remarks>The two travel together so the character the value is escaped with and the one the SQL declares are the
/// same. The wildcards are per vendor: SQL Server also reads <c>[</c> as one, and the standard allows an escape only
/// before a wildcard or itself.</remarks>
public readonly record struct LikeEscaping(char Escape, string Wildcards) {
  /// <summary>Represents a backslash escape and the two wildcards every server has.</summary>
  public static readonly LikeEscaping Default = new('\\', "%_");

  /// <summary>Builds the pattern for a value: its wildcards and escape characters escaped, and <c>%</c> added where
  /// asked.</summary>
  /// <param name="value">The search term, or <see langword="null"/> for an empty one.</param>
  /// <param name="leading"><see langword="true"/> to match anything before the term; otherwise,
  /// <see langword="false"/>.</param>
  /// <param name="trailing"><see langword="true"/> to match anything after the term; otherwise,
  /// <see langword="false"/>.</param>
  /// <returns>The pattern.</returns>
  public string BuildPattern(string? value, bool leading, bool trailing) {
    var pattern = new System.Text.StringBuilder();
    if (leading)
      pattern.Append('%');
    foreach (var c in value ?? "") {
      if (c == Escape || Wildcards.Contains(c))
        pattern.Append(Escape);
      pattern.Append(c);
    }
    if (trailing)
      pattern.Append('%');
    return pattern.ToString();
  }
}
