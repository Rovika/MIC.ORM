using System.Text;

namespace Mic.Orm.Model.Construction;

/// <summary>Represents one member of a parsed member list, with its sort direction.</summary>
public readonly struct MemberRef {
  public MemberRef(string name, bool descending) {
    Name = name;
    Descending = descending;
  }

  public string Name { get; }
  public bool Descending { get; }

  public override string ToString() => Descending ? "-" + Name : Name;
}

/// <summary>Represents the outcome of parsing one member list: the members it named, and the errors and warnings
/// about how it named them.</summary>
public sealed class MemberListResult {
  public List<MemberRef> Members { get; } = new();
  public List<string> Errors { get; } = new();
  public List<string> Warnings { get; } = new();

  public bool HasErrors => Errors.Count > 0;
}

/// <summary>Parses the member lists of the attribute vocabulary: <c>PrimaryKey</c>, <c>Index</c>, <c>Unique</c>,
/// <c>IncludeMembers</c>, <c>OrderBy</c>.</summary>
/// <remarks>Separators are <c>+</c>, <c>-</c>, <c>,</c> and whitespace; a separator run containing <c>-</c> marks the
/// member after it descending, so <c>"LastName -FirstName DateOfBirth"</c> is LastName ascending, FirstName descending,
/// DateOfBirth ascending. These characters survive a URL unencoded, and <c>+</c> arriving as a space parses the same.
/// Vita's <c>Name-desc</c> and <c>Name:DESC</c> forms, where the word marks the member before it, are accepted with a
/// deprecation warning, so <c>asc</c> and <c>desc</c> are reserved words rather than member names.</remarks>
public static class MemberListHelper {
  private const string Ascending = "asc";
  private const string Descending = "desc";

  // ':' and ';' are Vita's separators and earn a deprecation warning.
  private static bool IsSeparator(char c) => c is '+' or '-' or ',' or ':' or ';' || char.IsWhiteSpace(c);
  private static bool IsLegacySeparator(char c) => c is ':' or ';';

  /// <summary>Parses a member list.</summary>
  /// <param name="spec">The list as written in an attribute.</param>
  /// <param name="context">What is being parsed, for diagnostics, such as <c>"PrimaryKey on IPerson"</c>.</param>
  /// <param name="allowDirection"><see langword="true"/> to accept sort directions; <see langword="false"/> to report
  /// one, for a list where a direction is meaningless, such as an index's included members.</param>
  /// <returns>The members named, with the errors and warnings found.</returns>
  public static MemberListResult Parse(string? spec, string context, bool allowDirection = true) {
    var result = new MemberListResult();
    if (string.IsNullOrWhiteSpace(spec)) {
      result.Errors.Add($"{context}: the member list is empty.");
      return result;
    }

    var token = new StringBuilder();
    var pendingDescending = false;   // a '-' seen in the separator run before the next member
    var legacyReported = false;

    // A completed token is either a direction word, applying backwards, or a member name, taking the pending mark.
    for (var i = 0; i <= spec.Length; i++) {
      var atEnd = i == spec.Length;
      var ch = atEnd ? ' ' : spec[i];

      if (!atEnd && !IsSeparator(ch)) {
        token.Append(ch);
        continue;
      }

      if (token.Length > 0) {
        TakeToken(token.ToString(), ref pendingDescending, result, context, allowDirection);
        token.Clear();
      }

      if (atEnd)
        break;

      // Inside a separator run: '-' marks the next member, and a legacy separator is reported once.
      if (ch == '-')
        pendingDescending = true;
      if (IsLegacySeparator(ch) && !legacyReported) {
        legacyReported = true;
        result.Warnings.Add(
          $"{context}: '{ch}' as a separator is deprecated — use a comma, a space, or '-' for descending.");
      }
    }

    if (result.Members.Count == 0 && !result.HasErrors)
      result.Errors.Add($"{context}: the member list names no members.");

    var duplicate = result.Members
      .GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
      .FirstOrDefault(g => g.Count() > 1);
    if (duplicate != null)
      result.Errors.Add($"{context}: member '{duplicate.Key}' appears more than once.");

    return result;
  }

  private static void TakeToken(string token, ref bool pendingDescending, MemberListResult result, string context,
      bool allowDirection) {
    // 'asc' and 'desc' qualify the member already read, which is Vita's form.
    if (token.Equals(Ascending, StringComparison.OrdinalIgnoreCase)
        || token.Equals(Descending, StringComparison.OrdinalIgnoreCase)) {
      var wantsDescending = token.Equals(Descending, StringComparison.OrdinalIgnoreCase);
      if (result.Members.Count == 0) {
        result.Errors.Add($"{context}: '{token}' does not follow a member name.");
        pendingDescending = false;
        return;
      }
      if (!allowDirection)
        result.Errors.Add($"{context}: a sort direction is not meaningful here.");
      else
        result.Members[^1] = new MemberRef(result.Members[^1].Name, wantsDescending);
      result.Warnings.Add(
        $"{context}: '<member>-{token.ToLowerInvariant()}' is deprecated — write '-<member>' for descending.");
      pendingDescending = false;
      return;
    }

    if (pendingDescending && !allowDirection)
      result.Errors.Add($"{context}: a sort direction is not meaningful here.");

    result.Members.Add(new MemberRef(token, pendingDescending && allowDirection));
    pendingDescending = false;
  }
}
