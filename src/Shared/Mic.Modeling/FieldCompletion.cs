namespace Mic.Modeling;

/// <summary>Represents one completion candidate: the name to insert, a short type label for display, and whether
/// accepting it keeps the popup open for the next token.</summary>
/// <remarks><see cref="CanDrillIn"/> is <see langword="true"/> for an entity or entity-list field.
/// <see cref="InsertSuffix"/> is the text typed after the name on accept when the surrounding syntax needs one, such
/// as ": " after a CSS property; <see langword="null"/> means the caller's default, which for a field path is "." when
/// the item can drill in and nothing otherwise.</remarks>
public sealed record CompletionItem(string Name, string TypeLabel, bool CanDrillIn, string? InsertSuffix = null);

/// <summary>Provides field-path completion for a field-path input, such as the calculated-field editor's template
/// and path boxes.</summary>
/// <remarks>Extracting the token, a dotted run or the text inside <c>{…}</c>, is the UI's job; the completer takes
/// the token string.</remarks>
public static class FieldCompletion {
  /// <summary>Gets the fields of the entity a partial dotted path leads to whose names start with the path's trailing
  /// partial segment.</summary>
  /// <param name="root">The entity the path starts from, or <see langword="null"/>.</param>
  /// <param name="partialPath">The token being completed, such as "", "Add", "Address." or "Address.Ci".</param>
  /// <returns>The matching completion items; empty when <paramref name="root"/> is <see langword="null"/> or a
  /// segment of the path is unknown.</returns>
  public static IReadOnlyList<CompletionItem> Complete(EntityInfo? root, string? partialPath) {
    if (root == null)
      return Array.Empty<CompletionItem>();
    var segs = (partialPath ?? "").Split('.');
    var context = Walk(root, segs, segs.Length - 1);   // resolve all but the last segment to a context entity
    if (context == null)
      return Array.Empty<CompletionItem>();
    var partial = segs[^1];
    return context.Fields
      .Where(m => !m.IsDeleted && m.Name.StartsWith(partial, StringComparison.OrdinalIgnoreCase))
      .Select(m => new CompletionItem(m.Name, m.TypeRef.Name, CanDrillIn(m.TypeRef)))
      .ToList();
  }

  /// <summary>Walks the first <paramref name="count"/> segments from the root through entity and entity-list fields.
  /// Returns the entity whose fields are listed, or <see langword="null"/> when a segment is unknown or lands on a
  /// scalar.</summary>
  private static EntityInfo? Walk(EntityInfo root, string[] segs, int count) {
    var entity = root;
    for (var i = 0; i < count; i++) {
      var nextEntity = NextEntity(entity.FindField(segs[i])?.TypeRef);
      if (nextEntity == null)
        return null;
      entity = nextEntity;
    }
    return entity;
  }

  private static EntityInfo? NextEntity(TypeRef? t) =>
    (t as EntityTypeRef)?.Entity ?? ((t as ListTypeRef)?.ItemType as EntityTypeRef)?.Entity;

  private static bool CanDrillIn(TypeRef t) => NextEntity(t) != null;
}
