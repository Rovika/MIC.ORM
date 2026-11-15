using Mic.Common;
using Mic.Linq;
using Mic.Linq.Model;
using Mic.Linq.Nodes;
using Mic.Linq.Translation;
namespace Mic.Orm.Model;

/// <summary>Represents an entity: a table or a view, and the members that make it up.</summary>
/// <remarks><see cref="InterfaceType"/> is the declaring interface for an entity that came from C#, and
/// <see langword="null"/> for one that came from stored metadata.</remarks>
public class EntityTypeInfo : ILinqEntity {
  readonly List<EntityMemberInfo> _members = new();
  readonly Dictionary<string, EntityMemberInfo> _membersByName = new(StringComparer.OrdinalIgnoreCase);
  readonly List<EntityKeyInfo> _keys = new();
  readonly List<EntityCheckInfo> _checks = new();

  public EntityTypeInfo(string name, EntityModule module, Type? interfaceType = null) {
    Name = name;
    Module = module;
    InterfaceType = interfaceType;
  }

  public string Name { get; }

  /// <summary>Gets the module that declared this entity.</summary>
  public EntityModule Module { get; }

  public Type? InterfaceType { get; }

  /// <summary>Gets the events raised for rows of this type.</summary>
  /// <remarks>Hooked at startup, from the declaring module's <see cref="EntityModule.OnModelConstructed"/> or a
  /// service's <see cref="IServiceInit.Init"/>, never at request time.</remarks>
  public EntityEvents Events { get; } = new();

  /// <summary>Gets the former names of the table, from <c>[OldNames]</c>, by which an upgrade matches an existing
  /// table and renames it rather than dropping and recreating it.</summary>
  public IReadOnlyList<string> OldNames { get; internal set; } = [];

  /// <summary>Gets or sets the table name the developer asked for, from <c>[Entity(TableName = …)]</c>.</summary>
  public string? TableName { get; set; }
  public string? PluralName { get; set; }

  /// <summary>Gets or sets the resolved schema this entity's table lives in.</summary>
  /// <value>The schema, or <see langword="null"/> where the server has no schemas.</value>
  /// <remarks>Resolved by the builder from the module's schema or the application's default. Entity uniqueness is
  /// by schema and name, which <see cref="EntityAppModel.AddEntity"/> checks.</remarks>
  public EntitySchemaInfo? Schema { get; set; }

  /// <summary>Gets the definition of the view this entity reads through, or <see langword="null"/> for an entity
  /// stored in a table.</summary>
  public ViewInfo? View { get; internal set; }

  /// <summary>Gets a value that indicates whether this entity is a view: rows the server derives, which are
  /// read-only.</summary>
  public bool IsView => View != null;

  /// <summary>Gets a value that indicates whether this entity's rows live in a temp table a session creates and
  /// fills, as the module declared with <see cref="EntityModule.RegisterTempTable{TTemp}"/>.</summary>
  public bool IsTempTable { get; internal set; }

  /// <summary>Gets the function of the server this entity's rows come from, or <see langword="null"/> for an entity
  /// stored in a table.</summary>
  public TableFunctionInfo? TableFunction { get; internal set; }

  /// <summary>Gets a value that indicates whether this entity's rows are what a function of the server returns,
  /// read-only and read for the arguments of a call.</summary>
  public bool IsTableFunction => TableFunction != null;

  /// <summary>Gets a value that indicates whether the session can create, change or delete none of this entity's
  /// rows: a view's, which the server derives, a temp table's, which the fill writes once, or a function's.</summary>
  /// <remarks>The query engine's <see cref="ILinqEntity.IsReadOnly"/> is narrower: a temp table is a target of a
  /// statement, the fill from a query being one.</remarks>
  public bool IsReadOnly => IsView || IsTempTable || IsTableFunction;

  /// <summary>Gets what a read-only entity is, for a message: "a view", "a temp table" or "a table function".
  /// </summary>
  public string ReadOnlyKindName => IsView ? "a view" : IsTempTable ? "a temp table" : "a table function";

  public IReadOnlyList<EntityKeyInfo> Keys => _keys;

  /// <summary>Gets the check constraints the entity declares, every server's declarations together.</summary>
  public IReadOnlyList<EntityCheckInfo> Checks => _checks;

  /// <summary>Gets the key that identifies a row of this type.</summary>
  /// <remarks>Set by <see cref="AddKey"/> when the primary key is added.</remarks>
  public EntityKeyInfo? PrimaryKey { get; private set; }

  /// <summary>Gets the members of the entity, as one flat list.</summary>
  /// <remarks>There is no entity-level inheritance: the properties of the interface and of every interface it inherits
  /// are collected into this one list.</remarks>
  public IReadOnlyList<EntityMemberInfo> Members => _members;

  readonly List<EntityMemberInfo> _incomingReferences = [];

  /// <summary>Gets the reference members of any entity, this one included, that point at this entity.</summary>
  /// <remarks>Filled as each reference is resolved. This is how a delete check reaches every row that can point at a
  /// row of this type.</remarks>
  public IReadOnlyList<EntityMemberInfo> IncomingReferences => _incomingReferences;

  internal void AddIncomingReference(EntityMemberInfo member) => _incomingReferences.Add(member);

  /// <summary>Gets how many slots a record of this entity needs: the size of its value array and of its masks.
  /// </summary>
  /// <remarks>Slots are numbered by <see cref="EntityMemberInfo.DataIndex"/>; anything sizing an array indexed by it
  /// asks this, not <see cref="Members"/>.</remarks>
  public int DataCount { get; private set; }

  /// <summary>Gets the values a new record's value array starts with, copied into it wholesale.</summary>
  /// <remarks>Each non-nullable value-type member's slot holds its boxed default; a key member's slot stays
  /// <see langword="null"/>. The boxes are shared by every record of the entity, which is safe because a write
  /// replaces an entry and never mutates a box in place.</remarks>
  public object?[] InitialValues { get; private set; } = [];

  ulong _nameHash;
  ulong _referrersCacheKey;

  /// <summary>Gets the hash of this entity's name, computed once; half of what every identity key hashes to.</summary>
  public ulong NameHash => _nameHash;

  /// <summary>Gets the key the query cache holds the entity's delete check under: which entities have a row
  /// pointing at a given row of this one.</summary>
  public ulong ReferrersCacheKey => _referrersCacheKey != 0 ? _referrersCacheKey
    : _referrersCacheKey = Fnv1aHash.Combine(NameHash, "referrers");

  /// <summary>Gets the member whose value the database produces on insert, or <see langword="null"/> if there is
  /// none.</summary>
  public EntityMemberInfo? IdentityMember { get; private set; }

  /// <summary>Gets the members whose value comes from a database sequence, fetched by the save before the insert
  /// where the application assigned none.</summary>
  public IReadOnlyList<EntityMemberInfo> SequenceMembers { get; private set; } = [];

  /// <summary>Gets the member that counts the row's version, from <c>[RowVersion]</c>, or <see langword="null"/> if
  /// the entity declares none.</summary>
  /// <remarks>Matched on by every update and delete of the row and incremented by the update, so a save over a row
  /// somebody else changed since it was read finds no row and is refused as a concurrent update.</remarks>
  public EntityMemberInfo? RowVersionMember { get; private set; }

  /// <summary>Gets the position of this type in the dependency order of the whole model; a lower index is written
  /// first.</summary>
  /// <remarks>Assigned once, when the model is built. Types that reference each other in a cycle share an index; see
  /// <see cref="EntityTypeFlags.InTopologyGroup"/>.</remarks>
  public int TopologicalIndex { get; internal set; }

  /// <summary>Gets what the reference graph says about this type, assigned when the model is built.</summary>
  public EntityTypeFlags Flags { get; internal set; }

  /// <summary>Gets where this entity's rows are held whole in memory, as declared with
  /// <see cref="EntityApp.SetCached"/>.</summary>
  public CacheType CacheType { get; internal set; }

  /// <summary>Gets how long a cached block of this entity is trusted before the next read refills it, for rows
  /// something other than this application writes.</summary>
  /// <value>The expiration declared with <see cref="EntityApp.SetCached"/> or the application's
  /// <see cref="EntityApp.DefaultCacheExpiration"/>; <see langword="null"/> for a block that lives until this
  /// application writes the rows.</value>
  public TimeSpan? CacheExpiration { get; internal set; }

  /// <summary>Gets the conditions extensions attach to every use of this entity, in the order attached.</summary>
  /// <remarks>Each extension declares its own filter; they are ANDed where the source is resolved.</remarks>
  public IReadOnlyList<EntityFilter> Filters => _filters;
  private readonly List<EntityFilter> _filters = [];

  /// <summary>Attaches a condition to every use of this entity, as an extension does in
  /// <see cref="AppExtension.OnEntityBuilt"/>.</summary>
  /// <param name="filter">The condition, as a template over the entity's row from
  /// <see cref="QueryMaker.MakeFilter"/>.</param>
  /// <param name="cacheDiscriminator">A function that tells one filtered cache block from another, given the operation
  /// context. Required when the filter reads the context and the entity is cached; refused when the filter does not
  /// read the context.</param>
  /// <param name="owner">The type of the extension attaching the filter, which a query names to read without it; or
  /// <see langword="null"/> for a filter the application attaches directly, dropped only with every other.</param>
  /// <remarks>Must be called during <see cref="EntityApp.Init"/>, once the model is built and before the application
  /// is initialized. Whether the filter varies, by reading the operation context, is decided here from the template;
  /// the members it names are checked against each database when the db model is built.</remarks>
  /// <exception cref="InvalidOperationException">The application is already initialized, or the entity is not in
  /// an application's model.</exception>
  /// <exception cref="ArgumentException">A discriminator is given for a filter that does not vary.</exception>
  public void AddFilter(QueryTreeTemplate filter, Func<object?, string>? cacheDiscriminator = null, Type? owner = null) {
    ArgumentNullException.ThrowIfNull(filter);
    var app = Module.App;
    if (app?.Model == null || app.Initialized)
      throw new InvalidOperationException(
        $"'{Name}': a filter is attached during Init, from an extension's OnEntityBuilt, not before the model is built or after the application is initialized.");
    // A discriminator on a fixed filter would fragment the cache for nothing, so it is refused.
    var varies = filter.Body.ReadsContext();
    if (!varies && cacheDiscriminator != null)
      throw new ArgumentException(
        $"the filter on '{Name}' reads nothing from the operation context, so it is the same condition for every "
        + "caller and its cached blocks cannot differ — drop the discriminator, or make the filter read the context.",
        nameof(cacheDiscriminator));
    _filters.Add(new EntityFilter(filter, cacheDiscriminator, varies, owner));
  }

  /// <summary>Gets the members the ORM fills in when an entity of this type is created: its client-generated key.
  /// </summary>
  /// <remarks>The created-on and created-by stamps are in <see cref="AutoOnInsertMembers"/>, so that they carry the
  /// same instant as the updated stamps.</remarks>
  public IReadOnlyList<EntityMemberInfo> AutoOnCreateMembers { get; private set; } = [];

  /// <summary>Gets the members the ORM fills in when an entity of this type is inserted: its created-by and
  /// created-on stamps.</summary>
  public IReadOnlyList<EntityMemberInfo> AutoOnInsertMembers { get; private set; } = [];

  /// <summary>Gets the members the ORM fills in every time an entity of this type is saved: its updated-by and
  /// updated-on stamps.</summary>
  /// <remarks>A new row gets these too, so the two pairs agree on a row that has never been changed.</remarks>
  public IReadOnlyList<EntityMemberInfo> AutoOnSaveMembers { get; private set; } = [];

  /// <summary>Gets the members held as JSON: serialized on the way to the server, read back as objects, and compared
  /// as text at a save.</summary>
  public IReadOnlyList<EntityMemberInfo> JsonMembers { get; private set; } = [];

  /// <summary>Computes everything derived from the builders' decisions, once the entity is complete.</summary>
  /// <remarks>Called once, by the model builder, after every pass and before the db model is built; nothing here
  /// depends on a db model.</remarks>
  internal void CompleteSetup() {
    _nameHash = Fnv1aHash.Compute(Name);
    IdentityMember = _members.FirstOrDefault(m => m.AutoValue == AutoType.Identity);
    RowVersionMember = _members.FirstOrDefault(m => m.AutoValue == AutoType.RowVersion);
    SequenceMembers = [.. _members.Where(m => m.AutoValue == AutoType.Sequence)];

    AutoOnCreateMembers = [.. _members.Where(m => m.AutoValue == AutoType.NewGuid)];
    AutoOnInsertMembers = [.. _members.Where(m => m.AutoValue is
      AutoType.CreatedOn or AutoType.CreatedBy or AutoType.CreatedById)];
    AutoOnSaveMembers = [.. _members.Where(m => m.AutoValue is
      AutoType.UpdatedOn or AutoType.UpdatedBy or AutoType.UpdatedById)];
    JsonMembers = [.. _members.Where(m => m.IsJson)];

    foreach (var member in _members)
      member.CompleteSetup();

    // After the members, because it reads what their own setup decided.
    InitialValues = new object?[DataCount];
    MembersBySlot = new EntityMemberInfo[DataCount];
    foreach (var member in _members) {
      if (member.DataIndex < 0)
        continue;
      MembersBySlot[member.DataIndex] = member;
      if (!member.StartsEmpty)
        InitialValues[member.DataIndex] = member.DefaultValue;
    }
  }

  /// <summary>Gets the member whose value lives in each storage slot, the way back from a bit of a record's masks,
  /// which are indexed by slot, to the member it stands for.</summary>
  /// <remarks>Not <see cref="Members"/> indexed by the same number: a member that owns no slot sits in that list and
  /// takes no bit.</remarks>
  public EntityMemberInfo[] MembersBySlot { get; private set; } = [];

  /// <summary>Adds a member to the entity, assigning its position and its storage slot.</summary>
  /// <param name="member">The member to add; it belongs to no entity yet.</param>
  /// <remarks>Member names are unique ignoring case.</remarks>
  /// <exception cref="InvalidOperationException">The member already belongs to an entity, or the entity already has a
  /// member of that name.</exception>
  public void AddMember(EntityMemberInfo member) {
    ArgumentNullException.ThrowIfNull(member);
    if (member.Entity != null)
      throw new InvalidOperationException(
        $"Member '{member.Name}' already belongs to entity '{member.Entity.Name}'.");
    // Refused before anything is mutated, so a throw never leaves a slot handed out to a member that was not added.
    if (_membersByName.TryGetValue(member.Name, out var existing))
      throw new InvalidOperationException(
        $"Entity '{Name}': a member named '{existing.Name}' is already declared"
        + (existing.Name == member.Name ? "." : $" — member names are case-insensitive, and '{member.Name}' matches."));
    // Index says which member; DataIndex says where its value lives. They agree today because slots are handed out in
    // add order, and nothing may assume that: a queryable member takes a position and no slot.
    member.Index = _members.Count;
    member.DataIndex = member.IsQueryable ? -1 : DataCount++;
    _members.Add(member);
    _membersByName[member.Name] = member;
    member.Entity = this;
  }

  /// <summary>Finds a member by name, ignoring case.</summary>
  /// <param name="name">The member name.</param>
  /// <returns>The member, or <see langword="null"/> if none has that name.</returns>
  public EntityMemberInfo? FindMember(string name) =>
    _membersByName.TryGetValue(name, out var member) ? member : null;

  public EntityMemberInfo GetMember(string name) =>
    FindMember(name) ?? throw new KeyNotFoundException($"Member '{name}' not found on entity '{Name}'.");

  public void AddCheck(EntityCheckInfo check) =>
    _checks.Add(check ?? throw new ArgumentNullException(nameof(check)));

  public void AddKey(EntityKeyInfo key) {
    ArgumentNullException.ThrowIfNull(key);
    if (!ReferenceEquals(key.Entity, this))
      throw new InvalidOperationException($"Key '{key.Name}' was created for entity '{key.Entity.Name}', not '{Name}'.");
    if (key.IsPrimaryKey && PrimaryKey != null)
      throw new InvalidOperationException($"Entity '{Name}' already has a primary key ('{PrimaryKey.Name}').");
    _keys.Add(key);

    // Assigned here rather than at CompleteSetup, because the builder passes that follow need it.
    if (key.IsPrimaryKey)
      PrimaryKey = key;
  }

  // ----- ILinqEntity: the query engine's view -------------------------------------------------------------------------

  IReadOnlyList<ILinqEntityMember> ILinqEntity.Members => _members;

  ILinqEntityMember? ILinqEntity.FindMember(string name) => FindMember(name);

  IReadOnlyList<ILinqEntityMember> ILinqEntity.PrimaryKeyMembers =>
    PrimaryKey == null ? [] : PrimaryKey.GetMembers();

  bool ILinqEntity.IsTransient => false;

  bool ILinqEntity.IsCached => CacheType.HasFlag(CacheType.Server);

  bool ILinqEntity.IsReadOnly => IsView || IsTableFunction;

  /// <summary>Builds each filter's condition over a row, with the extension that attached it; a row has to satisfy
  /// all of them to be visible.</summary>
  /// <param name="row">The row the filters' templates are instantiated over.</param>
  /// <returns>The conditions, one per filter, in the order attached; empty if the entity has none.</returns>
  /// <remarks>Built once per database, when its db model prepares each table's filters.</remarks>
  public IReadOnlyList<(Type? Owner, QueryNode Condition)> BuildFilters(RowNode row) {
    ArgumentNullException.ThrowIfNull(row);
    return [.. _filters.Select(filter => (filter.Owner, filter.Template.Instantiate(QueryTreeTemplate.RowPlaceholder, row)))];
  }
}
