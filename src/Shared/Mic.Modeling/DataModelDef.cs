using Mic.Common;

namespace Mic.Modeling;

/// <summary>Represents a data model as an authored document: the JSON source of the model's own type definitions.
///</summary>
/// <remarks>The document and a <see cref="DataModelExtension"/> carry the same <see cref="StructureSpec"/> list and
/// apply it through <see cref="StructureSpec.ApplyAll"/>; the types here are owned by the core model. Framework
/// structures are not in the document; <see cref="FrameworkTypes"/> adds them to every registered model.</remarks>
public sealed class DataModelDef {
  public string Name { get; set; } = "";
  public string? Description { get; set; }
  public List<StructureSpec> Entities { get; set; } = new();

  /// <summary>Builds a live data model from this document.</summary>
  /// <param name="typeResolver">The resolver for type names, or <see langword="null"/>.</param>
  /// <param name="errors">When this method returns, contains the errors collected while building.</param>
  /// <returns>The model, minus whatever failed to build.</returns>
  /// <remarks>Errors are collected rather than thrown, so the rest of the document still loads.</remarks>
  public DataModel Build(TypeResolver? typeResolver, out IReadOnlyList<string> errors) {
    var errs = new List<string>();
    var model = new DataModel(Name);
    StructureSpec.ApplyAll(model, Entities, EntityKind.ModelPersisted, typeResolver, errs);
    errors = errs;
    return model;
  }

  /// <summary>Captures a live model as a document, the inverse of <see cref="Build"/>.</summary>
  /// <param name="model">The model to capture.</param>
  /// <returns>The document.</returns>
  /// <remarks>Only the model's own types are captured; framework structures, an extension's structures and
  /// calculated fields are skipped.</remarks>
  public static DataModelDef FromModel(DataModel model) {
    var def = new DataModelDef { Name = model.Name };
    foreach (var entity in model.Entities) {
      if (!entity.IsModelDefined)
        continue;
      var spec = new StructureSpec {
        Name = entity.Name, Base = entity.BaseEntity?.Name, IsStructure = entity.IsStructure,
      };
      foreach (var field in entity.Fields) {
        // An inherited field belongs to the base's spec; AddField forbids shadowing, so a name the base has is
        // inherited.
        if (field.IsCalculated || entity.BaseEntity?.FindField(field.Name) != null)
          continue;
        spec.Fields.Add(FieldSpecOf(field));
      }
      def.Entities.Add(spec);
    }
    return def;
  }

  private static StructureFieldSpec FieldSpecOf(Field field) {
    var spec = new StructureFieldSpec {
      Name = field.Name, Type = TypeSpec.Format(field.TypeRef),
      Label = field.Label, Description = field.Description,
      PrimaryKey = field.Flags.HasFlag(FieldFlags.PrimaryKey),
      Semantic = field.GetMetaData(KnownMetaKeys.Semantic, required: false) as string,
      MaxLength = field.GetMetaData(KnownMetaKeys.MaxLength, required: false) is int max ? max : 0,
    };
    if (field.GetMetaData(KnownMetaKeys.FkTargetEntity, required: false) is EntityTypeRef target) {
      spec.FkTarget = target.Entity.Name;
      spec.FkBacks = field.GetMetaData(KnownMetaKeys.FkBacksField, required: false) as string;
    }
    return spec;
  }
}

/// <summary>Represents one type definition in an authored document: a name, an optional base type and its own
/// fields.</summary>
/// <remarks>A model document's entities and an extension's structures are the same shape, told apart by who owns
/// them. Field types are in the text form of <see cref="TypeSpec"/>, because a document's types reference each other
/// before any of them exists.</remarks>
public sealed class StructureSpec {
  public string Name { get; set; } = "";
  public string? Base { get; set; }
  public string? Description { get; set; }

  /// <summary>Gets or sets a value that indicates whether the type is a support type that is not mapped to
  /// persistent storage.</summary>
  /// <remarks>The value is read in a model document, where entities are the default; an extension's types are all
  /// structures, and the value is ignored there.</remarks>
  public bool IsStructure { get; set; }

  public List<StructureFieldSpec> Fields { get; set; } = new();

  /// <summary>Gets the owner of a kind, with the storage question dropped: the core model owns both of its forms.
  /// </summary>
  static EntityKind OwnerOf(EntityKind kind) =>
    kind == EntityKind.ModelStructure ? EntityKind.ModelPersisted : kind;

  /// <summary>Applies a document's type definitions to a model in two passes: the first creates every type, so that
  /// every name in the document resolves; the second sets bases and adds fields.</summary>
  /// <param name="model">The model to apply to.</param>
  /// <param name="specs">The type definitions.</param>
  /// <param name="origin">The kind a non-structure gets, and the ownership test: a document may only rebuild a type
  /// whose origin matches its own.</param>
  /// <param name="typeResolver">The resolver for type names, or <see langword="null"/>.</param>
  /// <param name="errors">The list the errors are added to.</param>
  /// <remarks>A type already in the model is rebuilt in place rather than skipped, so a type reference held
  /// elsewhere keeps pointing at the live type. Errors are collected rather than thrown, so the rest of the document
  /// still loads.</remarks>
  public static void ApplyAll(DataModel model, IReadOnlyList<StructureSpec> specs, EntityKind origin,
                              TypeResolver? typeResolver, List<string> errors) {
    if (specs.Count == 0)
      return;

    foreach (var spec in specs) {
      if (string.IsNullOrWhiteSpace(spec.Name)) {
        errors.Add("A type has no name.");
        continue;
      }
      var kind = spec.IsStructure && origin == EntityKind.ModelPersisted ? EntityKind.ModelStructure : origin;
      var existing = model.FindEntity(spec.Name);
      if (existing == null) {
        model.AddEntity(new EntityInfo(spec.Name, kind));
        continue;
      }
      if (OwnerOf(existing.Kind) != OwnerOf(origin)) {
        errors.Add($"Type '{spec.Name}' collides with a type this document doesn't own — skipped.");
        continue;
      }
      if (!existing.IsExtensionDefined) {
        // A core entity's fields back live IEntityData storage that cannot be re-indexed here; a loader reports
        // rather than throws.
        errors.Add($"Type '{spec.Name}' is already in the model; rebuilding a core type in place needs a reload.");
        continue;
      }
      existing.ClearFields();      // rebuild in place on re-apply
    }

    foreach (var spec in specs) {
      if (model.FindEntity(spec.Name) is not { } type || OwnerOf(type.Kind) != OwnerOf(origin))
        continue;                  // named nothing, or the collision reported above
      if (!string.IsNullOrWhiteSpace(spec.Base)) {
        if (model.FindEntity(spec.Base!) is { } baseType) {
          if (type.BaseEntity == null) {                                       // on re-apply the base is already set
            // A hand-edited file can contain a cycle; SetBase throws on one, and the loader reports it instead.
            try { type.SetBase(baseType); }
            catch (InvalidOperationException ex) { errors.Add($"Type '{spec.Name}': {ex.Message}"); }
          }
          else if (type.BaseEntity != baseType)
            errors.Add($"Type '{spec.Name}': its base cannot be changed to '{spec.Base}' without a reload.");
        }
        else
          errors.Add($"Type '{spec.Name}': base type '{spec.Base}' is not in the model.");
      }
      foreach (var fieldSpec in spec.Fields)
        AddField(model, type, fieldSpec, typeResolver, errors);
    }
  }

  private static void AddField(DataModel model, EntityInfo type, StructureFieldSpec fieldSpec,
                       TypeResolver? typeResolver, List<string> errors) {
    if (string.IsNullOrWhiteSpace(fieldSpec.Name)) {
      errors.Add($"Type '{type.Name}': a field has no name.");
      return;
    }
    var typeRef = TypeSpec.Parse(fieldSpec.Type, model, typeResolver, out var typeError);
    if (typeRef == null) {
      errors.Add($"Type '{type.Name}', field '{fieldSpec.Name}': {typeError}.");
      return;
    }
    // Nullability comes from the resolved type, not from a '?' in the text, so a reference type is nullable too and
    // the flag matches the field's ScalarTypeRef.
    var flags = typeRef is ScalarTypeRef { IsNullable: true } or JsonTypeRef ? FieldFlags.Nullable : FieldFlags.None;
    if (fieldSpec.PrimaryKey)
      flags |= FieldFlags.PrimaryKey;

    var field = new Field(fieldSpec.Name, typeRef, flags) {
      Label = Blank(fieldSpec.Label), Description = Blank(fieldSpec.Description),
    };
    if (!string.IsNullOrWhiteSpace(fieldSpec.Semantic))
      field.SetMetaData(KnownMetaKeys.Semantic, fieldSpec.Semantic);
    if (fieldSpec.MaxLength > 0)
      field.SetMetaData(KnownMetaKeys.MaxLength, fieldSpec.MaxLength);

    if (!string.IsNullOrWhiteSpace(fieldSpec.FkTarget)) {
      if (model.FindEntity(fieldSpec.FkTarget!) is { } target) {
        field.Flags |= FieldFlags.ForeignKey;
        field.SetMetaData(KnownMetaKeys.FkTargetEntity, target.TypeRef);
        if (!string.IsNullOrWhiteSpace(fieldSpec.FkBacks))
          field.SetMetaData(KnownMetaKeys.FkBacksField, fieldSpec.FkBacks);
      }
      else
        // The field still lands, as a plain Guid without its lookup behaviour.
        errors.Add($"Type '{type.Name}', field '{fieldSpec.Name}': FK target '{fieldSpec.FkTarget}' is not in the model.");
    }
    type.AddField(field);
  }

  private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s!.Trim();
}

/// <summary>Represents one field of a type in an authored document.</summary>
/// <remarks><see cref="Type"/> is the text form of <see cref="TypeSpec"/>, such as "string", "DateTime?" or
/// "List&lt;SortSpec&gt;". <see cref="Semantic"/> is an optional <see cref="KnownSemantics"/> value that steers the
/// choice of control. <see cref="Label"/> and <see cref="Description"/> are the field's own caption and help text; a
/// structure has no model text pack, so without them a control falls back to a humanized identifier.</remarks>
public sealed class StructureFieldSpec {
  public string Name { get; set; } = "";
  public string Type { get; set; } = "";
  public string? Semantic { get; set; }
  public string? Label { get; set; }
  public string? Description { get; set; }

  public bool PrimaryKey { get; set; }

  /// <summary>Gets or sets the name of the entity the foreign key points at.</summary>
  /// <value>The entity name, or <see langword="null"/> for a field that is not a foreign key.</value>
  /// <remarks>The presence of a target is what marks the field as a foreign key.</remarks>
  public string? FkTarget { get; set; }
  /// <summary>Gets or sets the name of the navigation field the foreign key backs, such as "Address" for
  /// <c>AddressId</c>.</summary>
  public string? FkBacks { get; set; }

  /// <summary>Gets or sets the maximum length, in characters, of a text field.</summary>
  /// <value>The length, or 0 for unbounded.</value>
  public int MaxLength { get; set; }
}
