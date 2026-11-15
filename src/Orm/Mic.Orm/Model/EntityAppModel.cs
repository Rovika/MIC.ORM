using Mic.Linq.Model;
namespace Mic.Orm.Model;

/// <summary>Represents the entity model built from an <see cref="EntityApp"/>: its modules, schemas, sequences and
/// entities.</summary>
/// <remarks>The model classes are plain data; building and interrogating them is the work of builders and extension
/// methods. Modules are the declared <see cref="EntityModule"/> objects themselves; there is no metadata copy of a
/// module.</remarks>
public class EntityAppModel {
  public string Name { get; }

  readonly List<EntityTypeInfo> _entities = new();
  readonly List<EntityModule> _modules = new();
  readonly List<EntitySchemaInfo> _schemas = new();
  readonly List<SequenceInfo> _sequences = new();

  public EntityAppModel(string name, Version version) {
    Name = name;
    Version = version;
  }

  /// <summary>Gets the version of the application the model was built from, which the version info stored in a
  /// database identifies it by.</summary>
  public Version Version { get; }

  /// <summary>Gets the report of what building this model found and what it cost.</summary>
  /// <remarks>Readable after a failed build; <see cref="EntityApp.Init"/> keeps the model whether or not it is sound.
  /// </remarks>
  public EntityModelBuildReport BuildReport { get; } = new();

  /// <summary>Gets the factory of the emitted entity classes of this model, created on first use.</summary>
  /// <remarks>One per model. The dynamic assembly's name carries the model's name and a random suffix, so two models
  /// of one name in a process do not share an assembly name.</remarks>
  public Runtime.EntityClassFactory EntityClassFactory =>
    _entityClassFactory ??= new Runtime.EntityClassFactory(
      $"Mic.Orm.Entities.{Name}.{Guid.NewGuid().ToString("N")[..8]}");

  Runtime.EntityClassFactory? _entityClassFactory;

  /// <summary>Gets the declared modules this model was built from.</summary>
  /// <remarks>The upgrade manager records a version per module, so a per-module migration knows what it upgrades
  /// from.</remarks>
  public IReadOnlyList<EntityModule> Modules => _modules;

  public IReadOnlyList<EntitySchemaInfo> Schemas => _schemas;

  /// <summary>Gets the sequences the modules declared.</summary>
  public IReadOnlyList<SequenceInfo> Sequences => _sequences;

  /// <summary>Gets every entity of the model, across all modules.</summary>
  public IReadOnlyList<EntityTypeInfo> Entities => _entities;

  /// <summary>Records that this model was built from a module.</summary>
  /// <param name="module">The module to add.</param>
  /// <remarks>The module's entities are not taken from it; the builder adds each built entity with
  /// <see cref="AddEntity"/>.</remarks>
  public void AddModule(EntityModule module) {
    ArgumentNullException.ThrowIfNull(module);
    if (_modules.Any(m => m.Name == module.Name))
      throw new InvalidOperationException($"Model '{Name}': a module named '{module.Name}' is already added.");
    _modules.Add(module);
  }

  public EntitySchemaInfo GetOrAddSchema(string name) {
    var existing = _schemas.FirstOrDefault(s => s.Name == name);
    if (existing != null)
      return existing;
    var schema = new EntitySchemaInfo(name);
    _schemas.Add(schema);
    return schema;
  }

  public EntitySchemaInfo? FindSchema(string name) => _schemas.FirstOrDefault(s => s.Name == name);

  /// <summary>Finds the sequence of a name in a schema.</summary>
  /// <param name="name">The sequence name.</param>
  /// <param name="schema">The schema name; an empty string for a sequence with no schema.</param>
  /// <returns>The sequence, or <see langword="null"/> if the schema has none of that name.</returns>
  public SequenceInfo? FindSequence(string name, string schema) =>
    _sequences.FirstOrDefault(s =>
      string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase) && (s.Schema?.Name ?? "") == schema);

  /// <summary>Registers a sequence.</summary>
  /// <exception cref="InvalidOperationException">The sequence's schema already has a sequence of that name.</exception>
  public void AddSequence(SequenceInfo sequence) {
    ArgumentNullException.ThrowIfNull(sequence);
    if (FindSequence(sequence.Name, sequence.Schema?.Name ?? "") != null)
      throw new InvalidOperationException(
        $"Model '{Name}': a sequence named '{sequence.Name}' is already in schema '{sequence.Schema?.Name}'.");
    _sequences.Add(sequence);
  }

  readonly Dictionary<Type, EntityTypeInfo> _entitiesByType = [];
  readonly Dictionary<string, EntityTypeInfo[]> _entitiesByName = [];
  readonly Dictionary<Type, JsonTypeMap> _jsonTypesByBase = [];

  /// <summary>Finds the types a JSON member of a base type may hold, as the application declared them.</summary>
  /// <param name="baseType">The declared type of the JSON member.</param>
  /// <returns>The map, with the discriminators as declared and not yet converted to any column's type, or
  /// <see langword="null"/> if nothing was declared for the type.</returns>
  public JsonTypeMap? FindJsonTypes(Type baseType) => _jsonTypesByBase.GetValueOrDefault(baseType);

  /// <summary>Puts a module's JSON type declaration on the model.</summary>
  /// <param name="map">The map to add.</param>
  /// <exception cref="InvalidOperationException">The map's base type is already mapped.</exception>
  public void AddJsonTypes(JsonTypeMap map) {
    ArgumentNullException.ThrowIfNull(map);
    if (!_jsonTypesByBase.TryAdd(map.BaseType, map))
      throw new InvalidOperationException($"Model '{Name}': the JSON types of '{map.BaseType.Name}' are already mapped.");
  }

  /// <summary>Finds a module of this model by name.</summary>
  /// <param name="name">The name of the module.</param>
  /// <returns>The module, or <see langword="null"/> if none has that name.</returns>
  public EntityModule? FindModule(string name) => _modules.FirstOrDefault(m => m.Name == name);

  /// <summary>Finds the entity an interface declares.</summary>
  /// <param name="interfaceType">The entity interface.</param>
  /// <returns>The entity, or <see langword="null"/> if the interface declares none.</returns>
  /// <remarks>An entity defined in stored metadata has no interface and is not found this way.</remarks>
  public EntityTypeInfo? FindEntity(Type interfaceType) =>
    _entitiesByType.TryGetValue(interfaceType, out var entity) ? entity : null;

  /// <summary>Finds the entity of a name in a schema, the pair that uniqueness rests on.</summary>
  /// <param name="name">The entity name.</param>
  /// <param name="schema">The schema name; an empty string for an entity with no schema.</param>
  /// <returns>The entity, or <see langword="null"/> if the schema has none of that name.</returns>
  public EntityTypeInfo? FindEntity(string name, string schema) {
    if (!_entitiesByName.TryGetValue(name, out var namesakes))
      return null;
    foreach (var entity in namesakes)
      if ((entity.Schema?.Name ?? "") == schema)
        return entity;
    return null;
  }

  /// <summary>Finds the entity of a name, in whichever schema it is.</summary>
  /// <param name="name">The entity name.</param>
  /// <returns>The entity, or <see langword="null"/> if none has that name.</returns>
  /// <exception cref="InvalidOperationException">More than one schema has an entity of that name.</exception>
  public EntityTypeInfo? FindEntity(string name) {
    if (!_entitiesByName.TryGetValue(name, out var namesakes))
      return null;
    if (namesakes.Length > 1)
      throw new InvalidOperationException(
        $"Model '{Name}': '{name}' names an entity in more than one schema — "
        + $"{string.Join(", ", namesakes.Select(e => $"'{e.Schema?.Name}'"))}. Ask for it by schema.");
    return namesakes[0];
  }

  /// <summary>Registers an entity.</summary>
  /// <param name="entity">The entity to add.</param>
  /// <remarks>Entities are unique by schema and name; two schemas may each hold an entity of one name.</remarks>
  /// <exception cref="InvalidOperationException">The entity's schema already has an entity of that name.</exception>
  public void AddEntity(EntityTypeInfo entity) {
    var schema = entity.Schema?.Name ?? "";
    var sharing = _entitiesByName.TryGetValue(entity.Name, out var namesakes) ? namesakes : [];
    foreach (var other in sharing)
      if ((other.Schema?.Name ?? "") == schema)
        throw new InvalidOperationException(
          $"Model '{Name}': an entity named '{entity.Name}' is already in schema '{schema}'.");

    _entities.Add(entity);
    _entitiesByName[entity.Name] = [.. sharing, entity];
    if (entity.InterfaceType != null)
      _entitiesByType[entity.InterfaceType] = entity;
  }

  /// <summary>Removes a placeholder entity from the model and makes its interface resolve to the replacement.</summary>
  /// <remarks>The placeholder leaves the entity list and the name index, so nothing maps it to a table; a lookup by
  /// its interface through <see cref="FindEntity(Type)"/> returns the replacement.</remarks>
  internal void ReplaceEntity(EntityTypeInfo placeholder, EntityTypeInfo replacement) {
    _entities.Remove(placeholder);

    if (_entitiesByName.TryGetValue(placeholder.Name, out var sharing)) {
      var left = sharing.Where(e => !ReferenceEquals(e, placeholder)).ToArray();
      if (left.Length == 0)
        _entitiesByName.Remove(placeholder.Name);
      else
        _entitiesByName[placeholder.Name] = left;
    }

    if (placeholder.InterfaceType != null)
      _entitiesByType[placeholder.InterfaceType] = replacement;
  }
}
