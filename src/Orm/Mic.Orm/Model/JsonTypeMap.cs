using System.Globalization;

namespace Mic.Orm.Model;

/// <summary>Represents the types a JSON member of one base type may hold, each under its discriminator, as declared
/// with <c>EntityModule.MapJsonTypes</c>.</summary>
/// <remarks>A discriminator is whatever the column holds. The model builder converts the map once to the discriminator
/// column's own type with <see cref="ConvertTo"/>, so a lookup compares typed values. The serializer's in-document
/// polymorphism (<c>[JsonDerivedType]</c>, <c>$type</c>) needs no map.</remarks>
public sealed class JsonTypeMap {
  /// <summary>Gets the declared type of the members this map serves.</summary>
  public Type BaseType { get; }

  /// <summary>Gets the types, each under its discriminator, in the order declared.</summary>
  public IReadOnlyList<(object Discriminator, Type Type)> Entries { get; }

  readonly Dictionary<object, Type> _typesByDiscriminator = new();
  readonly Dictionary<Type, object> _discriminatorsByType = new();

  /// <summary>Initializes a new instance of the <see cref="JsonTypeMap"/> class.</summary>
  /// <param name="baseType">The declared type of the members the map serves.</param>
  /// <param name="entries">The types, each under its discriminator; each type is the base type or derived from it,
  /// and no discriminator or type occurs twice.</param>
  public JsonTypeMap(Type baseType, IReadOnlyList<(object Discriminator, Type Type)> entries) {
    ArgumentNullException.ThrowIfNull(baseType);
    ArgumentNullException.ThrowIfNull(entries);
    BaseType = baseType;
    Entries = entries;
    foreach (var (discriminator, type) in entries) {
      ArgumentNullException.ThrowIfNull(discriminator, nameof(entries));
      ArgumentNullException.ThrowIfNull(type, nameof(entries));
      if (!baseType.IsAssignableFrom(type))
        throw new ArgumentException($"'{type.Name}' is not a '{baseType.Name}'.", nameof(entries));
      if (!_typesByDiscriminator.TryAdd(discriminator, type))
        throw new ArgumentException($"Discriminator '{discriminator}' is declared twice for '{baseType.Name}'.", nameof(entries));
      if (!_discriminatorsByType.TryAdd(type, discriminator))
        throw new ArgumentException($"'{type.Name}' is declared twice for '{baseType.Name}'.", nameof(entries));
    }
  }

  /// <summary>Finds the type a discriminator names.</summary>
  /// <param name="discriminator">The discriminator, as read off the row.</param>
  /// <returns>The type, or <see langword="null"/> if the discriminator is <see langword="null"/> or not declared.
  /// </returns>
  public Type? FindType(object? discriminator) =>
    discriminator == null ? null : _typesByDiscriminator.GetValueOrDefault(discriminator);

  /// <summary>Finds the discriminator a type is stored under.</summary>
  /// <param name="type">The type.</param>
  /// <returns>The discriminator, or <see langword="null"/> if the type is not declared.</returns>
  public object? FindDiscriminator(Type type) => _discriminatorsByType.GetValueOrDefault(type);

  /// <summary>Creates the same map with every discriminator converted to the discriminator column's type, so that a
  /// value read off the row compares as itself.</summary>
  /// <param name="discriminatorType">The type of the discriminator column.</param>
  /// <returns>The converted map.</returns>
  /// <remarks>An enum is reached by name or by number, a number from its text. A discriminator that does not convert
  /// throws.</remarks>
  public JsonTypeMap ConvertTo(Type discriminatorType) {
    ArgumentNullException.ThrowIfNull(discriminatorType);
    var type = Nullable.GetUnderlyingType(discriminatorType) ?? discriminatorType;
    return new JsonTypeMap(BaseType, [.. Entries.Select(e => (ConvertDiscriminator(e.Discriminator, type), e.Type))]);
  }

  private static object ConvertDiscriminator(object discriminator, Type type) {
    if (type.IsInstanceOfType(discriminator))
      return discriminator;
    if (type.IsEnum)
      return discriminator is string name
        ? Enum.Parse(type, name, ignoreCase: true)
        : Enum.ToObject(type, discriminator);
    // An enum member given for a column that is not the enum: its number, unless text is wanted.
    if (discriminator is Enum && type != typeof(string))
      discriminator = Convert.ChangeType(discriminator, Enum.GetUnderlyingType(discriminator.GetType()), CultureInfo.InvariantCulture);
    return Convert.ChangeType(discriminator, type, CultureInfo.InvariantCulture);
  }
}
