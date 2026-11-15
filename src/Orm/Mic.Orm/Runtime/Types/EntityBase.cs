
namespace Mic.Orm.Runtime;

/// <summary>Provides the base class for every emitted entity class.</summary>
/// <remarks>The emitted class holds no state of its own; the <see cref="EntityRecord"/> holds every value. Casting an
/// entity to this type is the supported way to reach the record behind an interface.</remarks>
public abstract class EntityBase {
  protected EntityBase(EntityRecord record) {
    ArgumentNullException.ThrowIfNull(record);
    Record = record;
  }

  public EntityRecord Record { get; }

  public override string ToString() => Record.ToString();
}
