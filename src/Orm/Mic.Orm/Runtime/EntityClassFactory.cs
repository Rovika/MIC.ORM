using Mic.Emit;
using Mic.Orm.Model;
namespace Mic.Orm.Runtime;

/// <summary>Emits the runtime class behind each entity's interface, once per entity, and creates instances of
/// it.</summary>
public class EntityClassFactory {
  readonly ClassEmitter _emitter;
  readonly Dictionary<EntityTypeInfo, Type> _classes = [];
  readonly Lock _gate = new();

  /// <summary>Initializes a new instance of the <see cref="EntityClassFactory"/> class.</summary>
  /// <param name="assemblyName">The name of the dynamic assembly the classes are emitted into. It must be unique per
  /// factory in the process: two dynamic assemblies of one name coexist, but their types then share an
  /// assembly-qualified name.</param>
  public EntityClassFactory(string assemblyName) {
    ArgumentException.ThrowIfNullOrWhiteSpace(assemblyName);
    _emitter = new ClassEmitter(assemblyName);
  }

  /// <summary>Gets the class behind an entity's interface, emitting it on first use.</summary>
  /// <param name="entity">The entity whose class is wanted; it must have an interface type.</param>
  /// <returns>The emitted class, which implements the entity's interface and derives from
  /// <see cref="EntityBase"/>.</returns>
  public Type GetEntityClass(EntityTypeInfo entity) {
    ArgumentNullException.ThrowIfNull(entity);
    lock (_gate) {
      if (_classes.TryGetValue(entity, out var existing))
        return existing;

      var contract = entity.InterfaceType
        ?? throw new InvalidOperationException(
          $"Entity '{entity.Name}' has no interface — it came from stored metadata, and such an entity is served by "
          + "the untyped path rather than by an emitted class.");

      var emitted = _emitter.EmitClass(new EntityEmitController(entity), typeof(EntityBase), contract);
      _classes[entity] = emitted;
      return emitted;
    }
  }

  /// <summary>Creates an entity instance in front of an existing record.</summary>
  /// <param name="record">The record the instance reads and writes.</param>
  /// <returns>The new instance.</returns>
  /// <remarks>The instance is not put on the record; <see cref="EntityRecord.EntityInstance"/> creates its own by
  /// calling this method.</remarks>
  public EntityBase CreateInstance(EntityRecord record) {
    ArgumentNullException.ThrowIfNull(record);
    return (EntityBase)Activator.CreateInstance(GetEntityClass(record.Entity), record)!;
  }

  /// <summary>Creates a new entity with a record of its own that belongs to no session.</summary>
  /// <param name="entity">The entity type to create.</param>
  /// <returns>The new instance, fully loaded, with every member at the default of its type.</returns>
  /// <remarks>An entity made this way cannot resolve a reference, which needs a session's identity map;
  /// <see cref="EntitySession.NewEntity(EntityTypeInfo)"/> is the ordinary way to create one.</remarks>
  public EntityBase CreateInstance(EntityTypeInfo entity) {
    var record = new EntityRecord(entity);
    record.MarkFullyLoaded();
    return CreateInstance(record);
  }
}
