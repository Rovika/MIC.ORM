
namespace Mic.Orm.Model.Construction;

/// <summary>Specifies the runs the model builder makes over the attributes, each attribute acting in the one it
/// belongs to.</summary>
public enum AttributePhase {
  /// <summary>The run that settles the facts everything else reads: type, nullability, what a member is.</summary>
  Early,
  /// <summary>The run for the bulk of the attributes: sizes, keys, references, generated values.</summary>
  Middle,
  /// <summary>The run for whatever needs the rest to be settled, including checks that span several attributes.
  /// </summary>
  Late,
}
