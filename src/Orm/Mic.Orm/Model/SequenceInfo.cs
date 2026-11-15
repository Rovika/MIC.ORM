namespace Mic.Orm.Model;

/// <summary>Represents a database sequence a module declared: its name, its schema, where its values start and how
/// they step.</summary>
public class SequenceInfo {
  /// <summary>Initializes a new instance of the <see cref="SequenceInfo"/> class.</summary>
  public SequenceInfo(string name, EntitySchemaInfo? schema, EntityModule module) {
    ArgumentException.ThrowIfNullOrWhiteSpace(name);
    ArgumentNullException.ThrowIfNull(module);
    Name = name;
    Schema = schema;
    Module = module;
  }

  public string Name { get; }

  /// <summary>Gets the schema the sequence is in, or <see langword="null"/> where the model has none.</summary>
  public EntitySchemaInfo? Schema { get; }

  /// <summary>Gets the module that declared the sequence.</summary>
  public EntityModule Module { get; }

  /// <summary>Gets or sets the first value the sequence hands out.</summary>
  public long Start { get; set; } = 1;

  /// <summary>Gets or sets the step between values.</summary>
  public long Increment { get; set; } = 1;

  /// <summary>Gets or sets the type of the values.</summary>
  public Type DataType { get; set; } = typeof(long);

  public override string ToString() => Schema == null ? Name : $"{Schema.Name}.{Name}";
}
