using System.Text.Json.Serialization;

namespace Mic.Modeling;

/// <summary>Represents the base of every model element: a name plus a metadata bag that slices such as localization,
/// validation and UI hints attach to by key.</summary>
public abstract class ModelObject {
  public string Name { get; }

  // null until something is stored; most model objects never carry metadata
  Dictionary<string, object?>? _meta;

  protected ModelObject(string name) { Name = name; }

  /// <summary>Gets a value previously stored with <see cref="SetMetaData"/>.</summary>
  /// <param name="key">The key, one of the <see cref="KnownMetaKeys"/> values.</param>
  /// <param name="required"><see langword="true"/> to throw when the key is absent; otherwise, <see langword="false"/>.
  /// </param>
  /// <returns>The stored value, or <see langword="null"/> when the key is absent and <paramref name="required"/> is
  /// <see langword="false"/>.</returns>
  /// <exception cref="KeyNotFoundException">The key is absent and <paramref name="required"/> is
  /// <see langword="true"/>.</exception>
  public object? GetMetaData(string key, bool required = true) {
    if (_meta != null && _meta.TryGetValue(key, out var value))
      return value;
    if (required)
      throw new KeyNotFoundException($"Metadata key '{key}' not found on model object '{Name}'.");
    return null;
  }

  public void SetMetaData(string key, object? value) => (_meta ??= new())[key] = value;

  /// <summary>Gets the stored metadata, for a caller that walks the bag rather than asks for a key.</summary>
  /// <value>The stored pairs, or an empty dictionary when nothing was set.</value>
  /// <remarks>This property is not serialized; <see cref="StoredMetaData"/> is.</remarks>
  [JsonIgnore]
  public IReadOnlyDictionary<string, object?> MetaData =>
    _meta ?? (IReadOnlyDictionary<string, object?>)ImmutableEmptyMeta;

  /// <summary>Gets or sets the metadata bag as stored, which is the form that is serialized.</summary>
  /// <value>The bag, or <see langword="null"/> when nothing was set, so that a serializer that skips nulls writes
  /// nothing.</value>
  /// <remarks>The property is serialized under the name "MetaData". A bag read back from a document is where
  /// <see cref="MetaData"/> finds it.</remarks>
  [JsonPropertyName("MetaData")]
  public Dictionary<string, object?>? StoredMetaData {
    get => _meta;
    set => _meta = value;
  }

  static readonly Dictionary<string, object?> ImmutableEmptyMeta = new();
}

/// <summary>Represents a data model: a graph of entity definitions. Model-specific kinds derive from this class and
/// from the entity and field classes.</summary>
public class DataModel : ModelObject {
  readonly List<EntityInfo> _entities = new();

  public DataModel(string name) : base(name) { }

  /// <summary>When overridden in a derived class, gets the entities as a base-typed read-only list.</summary>
  /// <remarks>A derived class overrides this to expose its derived-typed entity list covariantly.</remarks>
  protected virtual IReadOnlyList<EntityInfo> EntitiesCore => _entities;
  public IReadOnlyList<EntityInfo> Entities => EntitiesCore;

  public virtual EntityInfo GetEntity(string name) =>
    FindEntity(name) ?? throw new KeyNotFoundException($"Entity '{name}' not found in model '{Name}'.");

  /// <summary>Gets the entity with the specified name, without throwing when the model has none.</summary>
  /// <param name="name">The entity name.</param>
  /// <returns>The entity, or <see langword="null"/> when the model has no entity of that name.</returns>
  public virtual EntityInfo? FindEntity(string name) => EntitiesCore.FirstOrDefault(e => e.Name == name);

  public void AddEntity(EntityInfo entity) => _entities.Add(entity);
}

/// <summary>Represents an entity definition: a named, structured type with fields, whose <see cref="TypeRef"/> other
/// fields can use as their type.</summary>
/// <remarks>Two kinds are told apart by <see cref="Kind"/>: an entity is mapped to a persistent table on the server,
/// and a structure is not; it exists to support the client and its conversation with the server.</remarks>
public class EntityInfo : ModelObject {
  readonly List<Field> _fields = new();

  public EntityInfo(string name, EntityKind kind = EntityKind.ModelPersisted) : base(name) {
    TypeRef = new EntityTypeRef(this);
    Kind = kind;
  }

  /// <summary>Gets this entity's own type reference.</summary>
  public EntityTypeRef TypeRef { get; }

  public EntityKind Kind { get; set; }

  /// <summary>Gets a value that indicates whether the entity is a support type, such as a search request or a filter,
  /// that is not mapped to persistent storage.</summary>
  public bool IsStructure => Kind != EntityKind.ModelPersisted;
  /// <summary>Gets a value that indicates whether the core model defines the entity, persisted or not.</summary>
  public bool IsModelDefined => Kind is EntityKind.ModelPersisted or EntityKind.ModelStructure;
  /// <summary>Gets a value that indicates whether the framework defines the entity, which can then be inherited from
  /// but not edited.</summary>
  public bool IsFrameworkDefined => Kind == EntityKind.Framework;
  /// <summary>Gets a value that indicates whether a model extension defines the entity, which is the only origin an
  /// editor can write.</summary>
  public bool IsExtensionDefined => Kind == EntityKind.Extension;

  /// <summary>Gets the entity this one derives from and inherits the fields of.</summary>
  /// <value>The base entity, or <see langword="null"/> for a root type.</value>
  public EntityInfo? BaseEntity { get; private set; }

  /// <summary>Sets the base entity.</summary>
  /// <param name="baseEntity">The entity to derive from.</param>
  /// <remarks>Call this before <see cref="AddField"/>: an own field's slot is assigned past the inherited ones.
  /// </remarks>
  /// <exception cref="InvalidOperationException">Own fields have already been added.
  /// -or-
  /// The base would form a cycle.</exception>
  public void SetBase(EntityInfo baseEntity) {
    if (_fields.Count > 0)
      throw new InvalidOperationException($"Entity '{Name}': the base type must be set before its own fields are added.");
    for (var ancestor = baseEntity; ancestor != null; ancestor = ancestor.BaseEntity)
      if (ReferenceEquals(ancestor, this))
        throw new InvalidOperationException($"Entity '{Name}': base '{baseEntity.Name}' would form a cycle.");
    BaseEntity = baseEntity;
  }

  /// <summary>When overridden in a derived class, gets this entity's own fields as a base-typed read-only list.
  /// </summary>
  /// <remarks>A derived class with a derived field type keeps its own typed list and overrides this to expose it
  /// covariantly, along with its own typed add method.</remarks>
  protected virtual IReadOnlyList<Field> FieldsCore => _fields;

  /// <summary>Gets all fields: the inherited ones first, at their base indices, then this entity's own.</summary>
  /// <remarks>The order makes an instance of a derived type storage-compatible with its base. A root type returns
  /// its own list without allocating; a derived type's list is recomputed on each call, so a later contribution to
  /// the base is seen.</remarks>
  public IReadOnlyList<Field> Fields {
    get {
      if (BaseEntity == null) return FieldsCore;
      var fields = new List<Field>(BaseEntity.Fields);
      fields.AddRange(FieldsCore);
      return fields;
    }
  }

  /// <summary>Gets the field with the specified name, walking the base chain, without throwing when no field has
  /// the name.</summary>
  /// <param name="name">The field name.</param>
  /// <returns>The field, or <see langword="null"/> when no field has that name.</returns>
  /// <remarks><see cref="GetField"/> is the throwing form, for a caller that treats a missing field as a contract
  /// violation.</remarks>
  public virtual Field? FindField(string name) =>
    FieldsCore.FirstOrDefault(m => m.Name == name) ?? BaseEntity?.FindField(name);

  public Field GetField(string name) =>
    FindField(name) ?? throw new KeyNotFoundException($"Field '{name}' not found on entity '{Name}'.");

  public void AddField(Field field) {
    // There is no field override in the model, so shadowing an inherited field is a build-time mistake.
    if (BaseEntity?.FindField(field.Name) != null)
      throw new InvalidOperationException($"Entity '{Name}': field '{field.Name}' is already inherited from '{BaseEntity.Name}'.");
    // The slot in an IEntityData's storage, past the inherited fields.
    field.DataIndex = InheritedFieldCount + _fields.Count;
    field.Entity = this;
    _fields.Add(field);
  }

  /// <summary>Gets the number of slots the base chain occupies, which is where an own field's index starts.</summary>
  private int InheritedFieldCount => BaseEntity?.Fields.Count ?? 0;

  /// <summary>Removes this entity's own fields, so that an extension can re-apply an edited definition in place.
  /// </summary>
  /// <remarks>Inherited fields belong to the base and remain. The entity object survives, so a type reference held
  /// elsewhere keeps pointing at the live type; existing data instances of the type become stale.</remarks>
  /// <exception cref="InvalidOperationException">The entity is not extension-defined.</exception>
  public void ClearFields() {
    if (!IsExtensionDefined)
      throw new InvalidOperationException($"Entity '{Name}': only an extension-defined type's fields may be cleared.");
    _fields.Clear();
  }

  /// <summary>Adds a calculated field, or replaces an existing calculated field of the same name in place.</summary>
  /// <param name="field">The calculated field.</param>
  /// <remarks>A calculated field has no storage slot, so adding one does not disturb the core fields' indices. A
  /// field whose name collides with a core field is skipped; an extension never shadows the core.</remarks>
  public void AddOrReplaceCalculatedField(Field field) {
    field.DataIndex = -1;
    field.Entity = this;
    var idx = _fields.FindIndex(m => m.Name == field.Name);
    if (idx >= 0) {
      if (!_fields[idx].IsCalculated)            // never replace a core field
        return;
      _fields[idx] = field;                     // in place, keeping its position
      return;
    }
    _fields.Add(field);
  }
}

/// <summary>Represents a field of an entity: a named member typed by a type reference, with flags.</summary>
/// <remarks>The semantic type and, for a foreign key, the target entity and the backed navigation field are in the
/// metadata bag under <see cref="KnownMetaKeys"/>.</remarks>
public class Field : ModelObject {
  public Field(string name, TypeRef typeRef, FieldFlags flags = FieldFlags.None) : base(name) {
    TypeRef = typeRef;
    Flags = flags;
  }

  /// <summary>Gets the field's type reference.</summary>
  /// <remarks>A derived class may set the value after construction; a single-token <see cref="TemplatedField"/>
  /// resolves its result type when the extension is applied.</remarks>
  public TypeRef TypeRef { get; protected set; }
  public FieldFlags Flags { get; set; }

  /// <summary>Gets or sets the caption the field carries itself.</summary>
  /// <value>The caption, or <see langword="null"/> for a core field, whose text is in the model text pack under
  /// "Entity.Field".</value>
  /// <remarks>A field the model text pack does not cover, such as a calculated field or a field of an extension's
  /// structure, names itself here, and consumers prefer the value over a humanized identifier. The text is in the
  /// source language and is not translatable until a text id is assigned to it.</remarks>
  public string? Label { get; set; }
  /// <summary>Gets or sets the help text the field carries itself, under the same rules as <see cref="Label"/>.
  /// </summary>
  public string? Description { get; set; }

  /// <summary>Gets the field's slot index into an entity data instance's storage, assigned by
  /// <see cref="EntityInfo.AddField"/> in field order.</summary>
  /// <value>The slot index, or -1 for a calculated field.</value>
  /// <remarks>A control caches the field and indexes the storage directly, with no name lookup.</remarks>
  public int DataIndex { get; internal set; }

  /// <summary>Gets a value that indicates whether the field is nullable.</summary>
  /// <remarks>This flag is authoritative. For a nullable value type the builder also sets it to match
  /// <c>ScalarTypeRef.IsNullable</c>; a reference field carries its nullability in the flag alone.</remarks>
  public bool IsNullable => Flags.HasFlag(FieldFlags.Nullable);

  /// <summary>Gets a value that indicates whether the field is calculated: contributed by a
  /// <see cref="DataModelExtension"/> and computed at read time.</summary>
  public bool IsCalculated => (Flags & FieldFlags.Calculated) != 0;
  /// <summary>Gets a value that indicates whether the field is a tombstone: deleted in the extension but kept so that
  /// a view that still references it knows it is gone.</summary>
  public bool IsDeleted => Flags.HasFlag(FieldFlags.Deleted);

  /// <summary>Gets the entity the field was added to.</summary>
  /// <remarks>A navigation field finds its foreign key, which is a separate field on the same entity, through this
  /// entity.</remarks>
  public EntityInfo? Entity { get; internal set; }
}

/// <summary>Specifies what a structured type is: whether the core model persists it, and which origin defines it.
/// </summary>
/// <remarks>Framework and extension types are always structures. The default is a persistent entity defined by the
/// core model.</remarks>
public enum EntityKind {
  /// <summary>The core model's own entity, mapped to a table on the server.</summary>
  ModelPersisted = 0,
  /// <summary>The core model's own support type, such as a filter or a request, that no table backs.</summary>
  ModelStructure,
  /// <summary>A structure the framework defines, which can be inherited from but not edited.</summary>
  Framework,
  /// <summary>A structure a model extension defines, the only editable origin.</summary>
  Extension,
}

/// <summary>Specifies the flags of a field.</summary>
[Flags]
public enum FieldFlags {
  None       = 0,
  Nullable   = 1,
  PrimaryKey = 1 << 1,
  ForeignKey = 1 << 2,
  /// <summary>A calculated field whose value is a template with <c>{path}</c> tokens.</summary>
  /// <remarks>The next bit is reserved for an expression kind.</remarks>
  Templated  = 1 << 3,
  /// <summary>The mask of the calculated field kinds, to be tested with a bitwise and rather than <c>HasFlag</c>.
  /// </summary>
  Calculated = Templated,
  /// <summary>A tombstone: a field removed from the extension but kept so that its referencers see it is gone.
  /// </summary>
  Deleted    = 1 << 5,
}

/// <summary>Provides the well-known keys of <see cref="ModelObject.GetMetaData"/> and
/// <see cref="ModelObject.SetMetaData"/>.</summary>
/// <remarks>Each key's summary states the runtime type of its value.</remarks>
public static class KnownMetaKeys {
  /// <summary>Represents a field's semantic type beyond its scalar, such as "Color", "Password", "Email" or
  /// "MultilineText", which the view builder uses to choose a control. The value is a string.</summary>
  public const string Semantic = "Semantic";

  /// <summary>Represents the referenced entity of a foreign-key field. The value is an <see cref="EntityTypeRef"/>.
  /// </summary>
  public const string FkTargetEntity = "FkTargetEntity";

  /// <summary>Represents the name of the navigation field a foreign-key field backs, such as "Address" for
  /// <c>AddressId</c>. The value is a string.</summary>
  public const string FkBacksField = "FkBacksField";

  /// <summary>Represents the maximum length, in Unicode characters, of a text field. The value is an integer.</summary>
  /// <remarks>The model is authoritative; a view may tighten the value but not loosen it. When the key is absent the
  /// length is unbounded and the UI applies its default cap.</remarks>
  public const string MaxLength = "MaxLength";
}
