using Mic.Common;

namespace Mic.Modeling;

/// <summary>Provides the compact text form of a type reference, which a model document, an extension package and a
/// view definition write: "string", "int?", "DateTime", "Person", "List&lt;SortSpec&gt;",
/// "List&lt;Mic.App.MaritalStatus&gt;".</summary>
/// <remarks>The grammar is <c>spec := "List&lt;" spec "&gt;" | name ["?"]</c>, where a name is a primitive alias, an
/// entity or structure name in the model, or a fully-qualified CLR type name; "?" marks a nullable value type and is
/// ignored on a reference type. A bare name is looked up in the model first, so a model type shadows a primitive
/// alias of the same name. The text stays unresolved until every type around it exists, so a document's types can
/// reference each other in any order.</remarks>
public static class TypeSpec {
  /// <summary>Holds the primitive aliases, so that a file says "string" rather than "System.String"; the type
  /// resolver resolves only fully-qualified names, so an application enum spells its namespace.</summary>
  static readonly Dictionary<string, Type> _aliases = new(StringComparer.OrdinalIgnoreCase) {
    ["string"] = typeof(string), ["bool"] = typeof(bool), ["char"] = typeof(char),
    ["byte"] = typeof(byte), ["short"] = typeof(short), ["int"] = typeof(int), ["long"] = typeof(long),
    ["float"] = typeof(float), ["double"] = typeof(double), ["decimal"] = typeof(decimal),
    ["Guid"] = typeof(Guid), ["DateTime"] = typeof(DateTime), ["DateOnly"] = typeof(DateOnly),
    ["TimeSpan"] = typeof(TimeSpan), ["DateTimeOffset"] = typeof(DateTimeOffset),
  };

  /// <summary>Gets the primitive alias names, common ones first, for a designer offering a choice of primitive type.
  /// </summary>
  /// <remarks><see cref="Parse"/> also accepts any fully-qualified CLR type name.</remarks>
  public static IReadOnlyList<string> PrimitiveNames { get; } = _aliases.Keys.ToList();

  /// <summary>Represents how a model document spells a JSON document field: "Json".</summary>
  public const string JsonName = "Json";

  /// <summary>Holds the alias of each CLR type that has one.</summary>
  static readonly Dictionary<Type, string> _aliasOf =
    _aliases.GroupBy(kv => kv.Value).ToDictionary(g => g.Key, g => g.First().Key);

  /// <summary>Formats a type reference as its text form.</summary>
  /// <param name="typeRef">The type reference.</param>
  /// <returns>The text form.</returns>
  /// <remarks>Formatting never fails; an unaliased scalar falls back to its full CLR name, which <see cref="Parse"/>
  /// reads back through the type resolver.</remarks>
  public static string Format(TypeRef typeRef) => typeRef switch {
    ListTypeRef list => $"List<{Format(list.ItemType)}>",
    EntityTypeRef entity => entity.Entity.Name,
    JsonTypeRef => JsonName,
    ScalarTypeRef scalar =>
      (_aliasOf.TryGetValue(scalar.UnderlyingType, out var alias) ? alias : scalar.UnderlyingType.FullName ?? scalar.UnderlyingType.Name)
      + (scalar.IsNullable && scalar.UnderlyingType.IsValueType ? "?" : ""),
    _ => typeRef.Name,
  };

  /// <summary>Composes a type spec from an item spec and whether the type is a list.</summary>
  /// <param name="itemSpec">The item type spec, or <see langword="null"/>.</param>
  /// <param name="isList"><see langword="true"/> to wrap the item in a list; otherwise, <see langword="false"/>.
  /// </param>
  /// <returns>The spec, or an empty string for an empty item spec.</returns>
  /// <remarks>An editor offers a type and a "List of" choice rather than the brackets; this method and
  /// <see cref="DecomposeList"/> work on the text alone, with no model.</remarks>
  public static string ComposeList(string? itemSpec, bool isList) =>
    string.IsNullOrEmpty(itemSpec) ? "" : isList ? $"List<{itemSpec}>" : itemSpec;

  /// <summary>Splits a type spec into its item spec and whether the type is a list.</summary>
  /// <param name="spec">The spec, or <see langword="null"/>.</param>
  /// <returns>The item spec and the list flag; an empty item spec for an empty spec.</returns>
  public static (string Item, bool IsList) DecomposeList(string? spec) {
    var text = spec?.Trim();
    if (string.IsNullOrEmpty(text))
      return ("", false);
    return text.StartsWith("List<", StringComparison.OrdinalIgnoreCase) && text.EndsWith(">", StringComparison.Ordinal)
      ? (text[5..^1].Trim(), true)
      : (text, false);
  }

  /// <summary>Parses a text form against a model, for entity and structure names, and a type resolver, for CLR
  /// scalars.</summary>
  /// <param name="spec">The text form, or <see langword="null"/>.</param>
  /// <param name="model">The model the entity and structure names are looked up in.</param>
  /// <param name="typeResolver">The resolver for fully-qualified CLR type names, or <see langword="null"/>, in which
  /// case only the primitive aliases and the model's types resolve.</param>
  /// <param name="error">When this method returns, contains the diagnostic, or <see langword="null"/> when there is
  /// none.</param>
  /// <returns>The type reference, or <see langword="null"/> when the type could not be resolved.</returns>
  /// <remarks>A bad type is a diagnostic rather than an exception. A "?" on an entity type is reported, and the type
  /// still resolves.</remarks>
  public static TypeRef? Parse(string? spec, DataModel model, TypeResolver? typeResolver, out string? error) {
    error = null;
    var text = spec?.Trim();
    if (string.IsNullOrEmpty(text)) {
      error = "the type is empty";
      return null;
    }

    if (text.StartsWith("List<", StringComparison.OrdinalIgnoreCase)) {
      if (!text.EndsWith(">", StringComparison.Ordinal)) {
        error = $"'{text}' is missing the closing '>'";
        return null;
      }
      var inner = text[5..^1];
      var itemType = Parse(inner, model, typeResolver, out error);
      return itemType == null ? null : new ListTypeRef(itemType);
    }

    var nullable = text.EndsWith("?", StringComparison.Ordinal);
    var name = nullable ? text[..^1].Trim() : text;

    // The model first: a model type wins over the alias table.
    if (model.FindEntity(name) is { } entity) {
      if (nullable) error = $"'{name}' is an entity type — '?' means nothing on it";   // reported, still resolves
      return entity.TypeRef;
    }
    // A JSON document is nullable by nature, so the marker says nothing.
    if (string.Equals(name, JsonName, StringComparison.OrdinalIgnoreCase))
      return new JsonTypeRef();

    if (_aliases.TryGetValue(name, out var aliased))
      return new ScalarTypeRef(nullable && aliased.IsValueType ? typeof(Nullable<>).MakeGenericType(aliased) : aliased);

    // Anything else is a fully-qualified CLR type, typically an app enum.
    if (typeResolver?.Resolve(name, nullable) is { } resolved)
      return new ScalarTypeRef(resolved);

    error = $"cannot resolve type '{name}' — expected a model type, a primitive ({string.Join(", ", _aliases.Keys.Take(5))}, …), or a fully-qualified CLR type name";
    return null;
  }
}
