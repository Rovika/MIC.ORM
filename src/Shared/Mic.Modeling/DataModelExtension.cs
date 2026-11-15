using System.Text.Json.Serialization;
using Mic.Common;

namespace Mic.Modeling;

/// <summary>Represents a package of metadata layered onto a core data model at runtime: per-entity contributions and
/// the structures the package defines.</summary>
/// <remarks>The package is a JSON-serializable specification; <see cref="Apply"/> builds and resolves the runtime
/// fields. It only adds to existing entities and never alters a core field. Only entities that contribute something
/// appear in <see cref="Entities"/>.</remarks>
public sealed class DataModelExtension {
  public string Name { get; set; } = "";
  public string Description { get; set; } = "";
  public string ModelName { get; set; } = "";      // the target core model, set at creation and not edited
  public List<EntityExtension> Entities { get; set; } = new();

  /// <summary>Gets or sets the structures the package defines: whole type definitions with no persistent table
  /// behind them, which support the client and its conversation with the server.</summary>
  public List<StructureSpec> Structures { get; set; } = new();

  /// <summary>Applies the extension to a model in place: lands the structures, then adds or replaces by name each
  /// calculated field of each entity.</summary>
  /// <param name="model">The model to apply to.</param>
  /// <param name="typeResolver">The resolver for type names, or <see langword="null"/>.</param>
  /// <returns>The errors collected while applying the structures.</returns>
  /// <remarks>Existing calculated fields are not cleared, so several packages compose and a later one overrides an
  /// earlier one's field of the same name; a core field is never replaced. A template can reference another
  /// calculated field of the same package. An entity not in the model is skipped. Re-applying the same package is
  /// idempotent; a field is removed by a <see cref="CalcFieldSpec.Deleted"/> tombstone, not by absence.</remarks>
  public IReadOnlyList<string> Apply(DataModel model, TypeResolver? typeResolver = null) {
    var errors = ApplyStructures(model, typeResolver);

    var built = new List<(CalculatedField field, EntityInfo owner)>();
    foreach (var entityExt in Entities) {
      var entity = model.FindEntity(entityExt.EntityName);
      if (entity == null)
        continue;
      foreach (var spec in entityExt.CalculatedFields) {
        var field = spec.Build();
        entity.AddOrReplaceCalculatedField(field);
        built.Add((field, entity));
      }
    }
    foreach (var (field, owner) in built)
      field.Resolve(owner, model);
    return errors;
  }

  /// <summary>Lands the structures in the model as extension-owned support types.</summary>
  private IReadOnlyList<string> ApplyStructures(DataModel model, TypeResolver? typeResolver) {
    var errors = new List<string>();
    StructureSpec.ApplyAll(model, Structures, EntityKind.Extension, typeResolver, errors);
    return errors;
  }
}

/// <summary>Represents one entity's contributions in an extension package.</summary>
/// <remarks><see cref="Validations"/> is a placeholder with no semantics yet.</remarks>
public sealed class EntityExtension {
  public string EntityName { get; set; } = "";
  public List<CalcFieldSpec> CalculatedFields { get; set; } = new();
  public List<ValidationRuleSpec> Validations { get; set; } = new();
}

/// <summary>Represents a placeholder for an entity validation rule, whose shape and semantics are not defined yet.
///</summary>
public sealed class ValidationRuleSpec {
}

/// <summary>Specifies the kind of a calculated field: a template with <c>{path}</c> tokens.</summary>
public enum CalcFieldKind { Templated }

/// <summary>Represents the serializable specification of one calculated field.</summary>
/// <remarks><see cref="Source"/> is the template, such as "{City}, {State}" or a bare "{Zip}" pass-through.
/// <see cref="Deleted"/> lands the field as a tombstone, kept so that its referencers see it is gone.</remarks>
public sealed class CalcFieldSpec {
  public string Name { get; set; } = "";

  [JsonConverter(typeof(JsonStringEnumConverter))]
  public CalcFieldKind Kind { get; set; }

  public string Source { get; set; } = "";
  public string? Label { get; set; }         // the display caption; rendering is not wired yet
  public string? Description { get; set; }    // the help text; rendering is not wired yet
  public bool Deleted { get; set; }

  public CalculatedField Build() {
    CalculatedField field = Kind switch {
      CalcFieldKind.Templated => new TemplatedField(Name, Source, Deleted),
      _ => throw new System.ArgumentOutOfRangeException(nameof(Kind), Kind, "Unknown calc field kind."),
    };
    field.Label = Label;
    field.Description = Description;
    return field;
  }
}
