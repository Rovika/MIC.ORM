using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Mic.Common;
using Mic.Linq.Model;

namespace Mic.Linq.Translation;

/// <summary>Provides what the translator knows about a JSON document held in a member: whether its type is an array
/// whose elements a query can read as rows, the shape those rows have, and the name a property carries inside the
/// document.</summary>
/// <remarks>The shapes are made once per model and element type, so that the lambda parameter a spread binds and the
/// rowset the spread reads share one entity.</remarks>
public static class JsonTranslationHelper {
  /// <summary>Specifies the name of the one column of the rows of a JSON array of scalars, which is the element
  /// itself.</summary>
  public const string JsonElementColumn = "value";

  static readonly ConcurrentDictionary<Type, IReadOnlyList<LinqMemberSpec>> _jsonSpecs = new();
  static readonly ConditionalWeakTable<ILinqDataModel, ConcurrentDictionary<Type, ILinqEntity>> _jsonShapes = new();

  /// <summary>Gets the element type of a JSON array type, whose values hold rows a query can read.</summary>
  /// <param name="type">The type of the member holding the document.</param>
  /// <returns>The element type of an enumerable of objects or of scalars, or <see langword="null"/> for any other
  /// type, a string and a byte array included.</returns>
  public static Type? GetJsonElementType(Type type) {
    ArgumentNullException.ThrowIfNull(type);
    if (type == typeof(string) || type == typeof(byte[]))
      return null;
    var enumerable = type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>) ? type
      : type.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
    return enumerable?.GetGenericArguments()[0];
  }

  /// <summary>Gets what the columns of the rows of a JSON array of an element type are made from, read off the type
  /// once.</summary>
  /// <param name="elementType">The type of the array's elements.</param>
  /// <returns>One column per public property that holds a scalar, under its JSON name; or for an array of scalars the
  /// one column <see cref="JsonElementColumn"/>.</returns>
  public static IReadOnlyList<LinqMemberSpec> GetJsonColumnSpecs(Type elementType) {
    ArgumentNullException.ThrowIfNull(elementType);
    return _jsonSpecs.GetOrAdd(elementType, static type => {
      if (type.IsScalarType())
        return [new LinqMemberSpec(JsonElementColumn, type)];
      var specs = new List<LinqMemberSpec>();
      foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)) {
        if (!property.CanRead || property.GetIndexParameters().Length > 0 || !property.PropertyType.IsScalarType())
          continue;
        var nullable = Nullable.GetUnderlyingType(property.PropertyType) != null || !property.PropertyType.IsValueType;
        specs.Add(new LinqMemberSpec(GetJsonName(property), property.PropertyType, nullable));
      }
      if (specs.Count == 0)
        throw new NotSupportedException($"'{type.Name}' has no scalar property to read the rows of a JSON array by.");
      return specs;
    });
  }

  /// <summary>Gets the entity the rows of a JSON array of an element type are rows of, made once per model and type
  /// from <see cref="GetJsonColumnSpecs"/>.</summary>
  /// <param name="model">The model that makes the entity.</param>
  /// <param name="elementType">The type of the array's elements.</param>
  /// <returns>The transient entity.</returns>
  public static ILinqEntity GetJsonShape(ILinqDataModel model, Type elementType) {
    ArgumentNullException.ThrowIfNull(model);
    ArgumentNullException.ThrowIfNull(elementType);
    return _jsonShapes.GetOrCreateValue(model).GetOrAdd(elementType, type =>
      model.CreateTransientEntity(type.IsScalarType() ? $"values of {type.Name}" : type.Name, GetJsonColumnSpecs(type)));
  }

  /// <summary>Gets the name a property has in a JSON document.</summary>
  /// <param name="member">The property.</param>
  /// <returns>The name a <c>JsonPropertyName</c> attribute gives, or the property's own name.</returns>
  public static string GetJsonName(MemberInfo member) {
    ArgumentNullException.ThrowIfNull(member);
    return member.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? member.Name;
  }
}
