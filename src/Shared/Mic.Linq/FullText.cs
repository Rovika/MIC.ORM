using Mic.Linq.Nodes;

namespace Mic.Linq;

/// <summary>Provides methods that match a row against a full-text query inside a query, which the translator writes
/// in the server's own spelling.</summary>
/// <remarks>These methods throw when called outside a query. A match is over the row's entity's full-text index, as
/// declared by an index attribute such as <c>[Index("Name", "Description", IndexType = DbIndexType.FullText)]</c>; an
/// entity with no such index, or with several, is refused by name. The query is plain words whose meaning the
/// <see cref="TextMatchKind"/> specifies alike on every server; the words are bound as a parameter, so nothing a user
/// types can become a query operator.</remarks>
public static class FullText {
  /// <summary>Determines whether a row matches a full-text query.</summary>
  /// <typeparam name="T">The entity type of the row.</typeparam>
  /// <param name="row">The row to match.</param>
  /// <param name="query">The words to match.</param>
  /// <param name="kind">One of the enumeration values that specifies how the words are matched. The default matches
  /// every word.</param>
  /// <param name="distance">For <see cref="TextMatchKind.Near"/>, the greatest distance between the words.</param>
  /// <returns><see langword="true"/> if the row matches the query; otherwise, <see langword="false"/>.</returns>
  public static bool Matches<T>(T row, string query, TextMatchKind kind = TextMatchKind.AllWords, int distance = 0)
      where T : class =>
    throw MakeNotCallable(nameof(Matches));

  /// <summary>Returns how well a row matches a full-text query.</summary>
  /// <typeparam name="T">The entity type of the row.</typeparam>
  /// <param name="row">The row to rank.</param>
  /// <param name="query">The words to match.</param>
  /// <param name="kind">One of the enumeration values that specifies how the words are matched.</param>
  /// <param name="distance">For <see cref="TextMatchKind.Near"/>, the greatest distance between the words.</param>
  /// <returns>A number that is higher for a better match.</returns>
  /// <remarks>Ranks are comparable within one query only, since each server ranks its own way.</remarks>
  public static double Rank<T>(T row, string query, TextMatchKind kind = TextMatchKind.AllWords, int distance = 0)
      where T : class =>
    throw MakeNotCallable(nameof(Rank));

  private static InvalidOperationException MakeNotCallable(string name) =>
    new($"{nameof(FullText)}.{name} is written inside a query, where the server matches the text; it does not run in memory.");
}
