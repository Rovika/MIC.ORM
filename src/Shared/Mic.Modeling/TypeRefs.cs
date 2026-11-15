namespace Mic.Modeling;

/// <summary>Specifies what a field's type ultimately is: a single value, an entity defined in the model, a list of an
/// item type, or a JSON document.</summary>
/// <remarks>The kind also says what a data path must resolve to: a container scope wants an entity, a grid's rows
/// want a list, and a field's value wants a scalar.</remarks>
public enum TypeKind { Scalar, Entity, List, Json }

/// <summary>Represents a shareable reference to the type of a model field, and provides a base for the concrete
/// kinds.</summary>
/// <remarks>A type reference is not a type definition; one reference can serve many fields. A model-specific kind
/// may add its own type references.</remarks>
public abstract class TypeRef {
  public string Name { get; }
  public TypeKind Kind { get; }
  protected TypeRef(string name, TypeKind kind) { Name = name; Kind = kind; }
}

/// <summary>Represents a single value type, such as a string, an integer, a Guid, a date or an enum.</summary>
public class ScalarTypeRef : TypeRef {
  /// <summary>Gets the CLR type as supplied, which may be a nullable value type.</summary>
  public Type Type { get; }
  /// <summary>Gets the non-nullable underlying type, which is <see cref="Type"/> except for a nullable value type.
  /// </summary>
  public Type UnderlyingType { get; }
  /// <summary>Gets a value that indicates whether the type is a reference type or a nullable value type.</summary>
  /// <remarks>A field's authoritative nullability is <see cref="Field.IsNullable"/>, which the builder sets to match
  /// this value.</remarks>
  public bool IsNullable => !Type.IsValueType || Type != UnderlyingType;

  public ScalarTypeRef(Type type) : base((Nullable.GetUnderlyingType(type) ?? type).Name, TypeKind.Scalar) {
    Type = type;
    UnderlyingType = Nullable.GetUnderlyingType(type) ?? type;
  }
}

/// <summary>Represents a reference to an entity in the same model: the type of a field whose value is an entity.
/// </summary>
public class EntityTypeRef : TypeRef {
  /// <summary>Gets the referenced entity.</summary>
  public EntityInfo Entity { get; }
  public EntityTypeRef(EntityInfo entity) : base(entity.Name, TypeKind.Entity) { Entity = entity; }
}

/// <summary>Represents a collection of an item type, such as a list of phones.</summary>
/// <remarks>The item type is usually an <see cref="EntityTypeRef"/>, but any kind is allowed.</remarks>
public class ListTypeRef : TypeRef {
  public TypeRef ItemType { get; }
  public ListTypeRef(TypeRef itemType) : base("List<" + itemType.Name + ">", TypeKind.List) { ItemType = itemType; }
}

/// <summary>Represents a JSON document: an object or an array the model does not describe field by field, held
/// whole.</summary>
/// <remarks>On the client the value is a <c>System.Text.Json.Nodes.JsonNode</c>; in a store it is the server's JSON
/// column. A model document spells the type "Json", and the value editor is the JSON editor.</remarks>
public class JsonTypeRef : TypeRef {
  public JsonTypeRef() : base("Json", TypeKind.Json) { }
}
