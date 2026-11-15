namespace Mic.Modeling;

/// <summary>Represents an object identified within a model: the group it belongs to, and its name there.</summary>
/// <remarks>The group is the model's own organising unit, within which a name means one thing; for a database model
/// and for an entity model it is the schema. A member takes no group, because it is unique within its object.</remarks>
public readonly record struct ModelObjectRef(string? Group, string Name) {
  public override string ToString() => Group == null ? Name : $"{Group}.{Name}";
}

/// <summary>Represents one member of an object and the member of the other object it corresponds to.</summary>
public readonly record struct MemberMappingDef(string A, string B) {
  public override string ToString() => $"{A} ↔ {B}";
}

/// <summary>Represents one object in each model, and how their members correspond.</summary>
public sealed class ObjectMappingDef {
  public ObjectMappingDef(ModelObjectRef a, ModelObjectRef b) {
    A = a;
    B = b;
  }

  /// <summary>Gets the object in <see cref="ModelMappingDef.ModelA"/>.</summary>
  public ModelObjectRef A { get; }

  /// <summary>Gets the object in <see cref="ModelMappingDef.ModelB"/>.</summary>
  public ModelObjectRef B { get; }

  public List<MemberMappingDef> Members { get; set; } = [];

  public override string ToString() => $"{A} ↔ {B} ({Members.Count} members)";
}

/// <summary>Represents a mapping between two models: which object corresponds to which, and which member to which.
///</summary>
/// <remarks>A model names nothing outside itself; the mapping is what connects it to another model, stored and shown
/// in its own right. The mapping is not directional: a correspondence reads the same either way, which is what the
/// A and B naming says.</remarks>
public sealed class ModelMappingDef {
  /// <summary>Gets or sets the name of the first model.</summary>
  /// <remarks>A mapping refers to its two models by name and holds neither.</remarks>
  public string? ModelA { get; set; }

  /// <summary>Gets or sets the name of the second model.</summary>
  public string? ModelB { get; set; }

  public List<ObjectMappingDef> Objects { get; set; } = [];

  public override string ToString() => $"{ModelA} ↔ {ModelB}: {Objects.Count} objects";
}
