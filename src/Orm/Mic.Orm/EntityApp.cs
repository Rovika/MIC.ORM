using Mic.Orm.Db;
using Mic.Orm.Db.Model.Upgrades;
using Mic.Orm.Logging;
using Mic.Orm.Model;
using Mic.Orm.Runtime;
using Mic.Orm.Services;

namespace Mic.Orm;

/// <summary>Represents an application: its modules, services, settings and declarations, and, once built and
/// connected, the model those declarations produced and the databases it talks to.</summary>
/// <remarks>The declarations are recorded here and applied by the model builder during <see cref="Init"/>; none of
/// them is validated at the call. Databases are registered with <see cref="IDataAccessService"/> and resolved by
/// name from an operation's context; there is no single database property.</remarks>
public class EntityApp : IAsyncDisposable {

  public string Name { get; }

  /// <summary>Gets the version of the application, recorded in <see cref="EntityAppModel.Version"/> when the model is
  /// built and stored in a database to identify it.</summary>
  public Version Version { get; }

  /// <summary>Gets the settings that control how the model is built.</summary>
  /// <remarks>The settings are read during <see cref="Init"/> and not afterwards.</remarks>
  public EntityAppSettings Settings { get; }

  /// <summary>Gets the time service of this application.</summary>
  /// <remarks>Resolved on every read, so a service registered after construction takes effect.</remarks>
  public ITimeService TimeService => GetService<ITimeService>();

  /// <summary>Gets the service that holds the databases this application talks to, by name.</summary>
  public IDataAccessService DataAccess => GetService<IDataAccessService>();

  /// <summary>Gets the log service every operation's log is posted to.</summary>
  /// <remarks>With no listener and <see cref="EntityAppSettings.LogMode"/> at <see cref="LogMode.None"/>, nothing is
  /// posted.</remarks>
  public ILogService LogService => GetService<ILogService>();

  /// <summary>Gets or sets the service provider of the host, such as the root provider of its dependency injection
  /// container.</summary>
  /// <value>The host's provider, or <see langword="null"/> where the host gives none.</value>
  /// <remarks>Searched after the application's own services. A scoped service is not resolved from it; an operation
  /// reaches one through <see cref="OperationContext.Services"/>.</remarks>
  public IServiceProvider? HostServices { get; set; }

  /// <summary>Gets the events raised for sessions and saves, across every entity type.</summary>
  /// <remarks>The per-type events are on <see cref="EntityTypeInfo.Events"/>.</remarks>
  public EntityAppEvents Events { get; } = new();

  /// <summary>Gets the events raised for this application's databases: connecting, upgrading, and a request for one
  /// that is not registered.</summary>
  public DatabaseEvents DatabaseEvents { get; } = new();

  public IReadOnlyList<EntityModule> Modules => _modules;

  /// <summary>Gets the model built from this application's declarations, or <see langword="null"/> until
  /// <see cref="Init"/> has built it.</summary>
  /// <remarks>Set before the extensions see the entities, so a filter they attach can bind against it, and kept
  /// whether or not the model built cleanly; <see cref="Initialized"/> says whether it did.</remarks>
  public EntityAppModel? Model { get; internal set; }

  /// <summary>Gets a value that indicates whether the model is built without errors.</summary>
  /// <remarks>This is what <see cref="Init"/> returns and what <see cref="ConnectToAsync"/> requires.</remarks>
  public bool Initialized { get; internal set; }

  /// <summary>Gets a value that indicates whether a database has been reached and is fit to serve.</summary>
  /// <remarks>Set at the end of <see cref="ConnectToAsync"/> and stays <see langword="true"/> once set, even if a
  /// later connection to another database fails.</remarks>
  public bool IsConnected { get; internal set; }

  /// <summary>Gets a value that indicates whether <see cref="ShutdownAsync"/> has run.</summary>
  public bool IsShutDown { get; private set; }

  /// <summary>Gets the token that is cancelled when the application shuts down, for work the application runs in the
  /// background.</summary>
  public CancellationToken ShutdownToken => _shutdown.Token;

  /// <summary>Gets or sets the first fault that occurred at startup, with its exception.</summary>
  /// <remarks>Never overwritten once set; <see cref="ConnectToAsync"/> reports it as the reason it cannot proceed.
  /// </remarks>
  internal (string Message, Exception? Error)? StartupError { get; set; }

  /// <summary>Gets the cache declarations recorded by <see cref="SetCached"/>, for the model builder to apply.
  /// </summary>
  internal IReadOnlyList<CacheDeclaration> CachedEntities => _cachedEntities;

  /// <summary>Gets the extensions added with <see cref="AddExtension"/>, in the order they were added.</summary>
  public IReadOnlyList<AppExtension> Extensions => _extensions;

  readonly List<EntityModule> _modules = [];
  readonly Dictionary<Type, object> _services = [];
  readonly List<CacheDeclaration> _cachedEntities = [];
  readonly List<AppExtension> _extensions = [];

  readonly List<ReplacementDeclaration> _entityReplacements = [];
  readonly CancellationTokenSource _shutdown = new();

  /// <summary>Initializes a new instance of the <see cref="EntityApp"/> class.</summary>
  /// <param name="name">The name of the application.</param>
  /// <param name="version">The version of the application.</param>
  /// <param name="settings">The settings that control how the model is built, or <see langword="null"/> for the
  /// defaults.</param>
  /// <remarks>The default services are registered by the constructor; an application replaces one with
  /// <see cref="RegisterService{TService}"/>.</remarks>
  public EntityApp(string name, Version version, EntityAppSettings? settings = null) {
    Name = name;
    Version = version;
    Settings = settings ?? new EntityAppSettings();

    RegisterService<ITimeService>(new TimeService(this));
    RegisterService<IGuidFactory>(new GuidFactory(this));
    RegisterService<IDataAccessService>(new DataAccessService(this));
    RegisterService<ILogService>(new LogService());
  }

  /// <summary>Builds the model from this application's declarations.</summary>
  /// <returns>The build report, with the warnings if there are any.</returns>
  /// <remarks>Touches no database and names no provider. The model is kept on <see cref="Model"/> whether or not it
  /// built, so a failed one can be inspected; <see cref="Initialized"/> says whether anything may run on it.</remarks>
  /// <exception cref="InvalidOperationException">The model has errors, or an application hook threw; the message
  /// carries the report.</exception>
  public EntityModelBuildReport Init() => AppInitHelper.Initialize(this);

  /// <summary>Asynchronously connects to a database and brings its schema into line with the model.</summary>
  /// <param name="settings">The settings of the database to connect to.</param>
  /// <param name="cancellation">A token that cancels the operation.</param>
  /// <returns>A task that represents the asynchronous operation. The task result contains the upgrade report: what
  /// the database was, what the connect changed, and what it left as it was.</returns>
  /// <remarks>Requires <see cref="Initialized"/>. May be called more than once, for instance to connect a tenant's
  /// database during a request. A schema the <see cref="DbSettings.UpgradeMode"/> does not let the connect change
  /// is reported, not thrown: under <see cref="DbUpgradeMode.NonProductionOnly"/> the host takes the responsibility,
  /// and the report's changes say what differs. Under <see cref="DbUpgradeMode.Never"/> the database is not read
  /// and the report holds no changes.</remarks>
  /// <exception cref="InvalidOperationException">The application did not initialize.</exception>
  /// <exception cref="DataAccessException">The model cannot be mapped to the provider, the server cannot be reached,
  /// or the upgrade ran and failed.</exception>
  public Task<DbUpgradeReport> ConnectToAsync(DbSettings settings, CancellationToken cancellation = default) =>
    AppInitHelper.ConnectToAsync(this, settings, cancellation);

  /// <summary>Asynchronously shuts the application down: raises <see cref="EntityAppEvents.Shutdown"/>, cancels
  /// <see cref="ShutdownToken"/>, flushes the log and marks the application disconnected.</summary>
  /// <returns>A task that represents the asynchronous operation.</returns>
  /// <remarks>Runs once; a second call does nothing. A session opened afterwards still works: the databases stay
  /// registered and the pools close with the process. The shutdown is for what the application itself must end
  /// before the process does: its background work, and the last batch of the log.</remarks>
  public async Task ShutdownAsync() {
    if (IsShutDown)
      return;
    IsShutDown = true;
    Events.OnShutdown(this);
    await _shutdown.CancelAsync();
    await LogService.FlushAsync();
    IsConnected = false;
  }

  /// <summary>Shuts the application down; the synchronous form of <see cref="ShutdownAsync"/>.</summary>
  public void Shutdown() => SyncOverAsyncHelper.Run(ShutdownAsync);

  /// <summary>Asynchronously shuts the application down, as <see cref="ShutdownAsync"/> does.</summary>
  public async ValueTask DisposeAsync() {
    await ShutdownAsync();
    GC.SuppressFinalize(this);
  }

  /// <summary>Connects to a database and brings its schema into line with the model.</summary>
  /// <param name="settings">The settings of the database to connect to.</param>
  /// <returns>The upgrade report.</returns>
  /// <remarks>The synchronous form of <see cref="ConnectToAsync"/>, for a console host.</remarks>
  public DbUpgradeReport ConnectTo(DbSettings settings) => SyncOverAsyncHelper.Run(() => ConnectToAsync(settings));

  /// <summary>Adds a module, optionally placing it in a schema.</summary>
  /// <param name="module">The module to add.</param>
  /// <param name="schema">The schema the module's entities go in, or <see langword="null"/> to leave the module's own
  /// setting.</param>
  /// <remarks>A module's schema can be reassigned until <see cref="Init"/>.</remarks>
  public void AddModule(EntityModule module, string? schema = null) {
    ArgumentNullException.ThrowIfNull(module);
    if (module.App != null)
      throw new InvalidOperationException(
        $"Module '{module.Name}' already belongs to application '{module.App.Name}'.");
    if (_modules.Any(m => m.Name == module.Name))
      throw new InvalidOperationException($"Application '{Name}': a module named '{module.Name}' is already added.");
    module.App = this;
    if (schema != null)
      module.Schema = schema;
    _modules.Add(module);
  }

  /// <summary>Finds a module of this application by name.</summary>
  /// <param name="name">The name of the module.</param>
  /// <returns>The module, or <see langword="null"/> if none has that name.</returns>
  public EntityModule? FindModule(string name) => _modules.FirstOrDefault(m => m.Name == name);

  /// <summary>Places several modules in one schema.</summary>
  /// <param name="schema">The schema the modules' entities go in.</param>
  /// <param name="modules">The modules to place in the schema.</param>
  public void AssignSchema(string schema, params EntityModule[] modules) {
    ArgumentException.ThrowIfNullOrWhiteSpace(schema);
    ArgumentNullException.ThrowIfNull(modules);
    foreach (var module in modules) {
      ArgumentNullException.ThrowIfNull(module);
      module.Schema = schema;
    }
  }

  /// <summary>Declares that <paramref name="replacement"/> stands for <paramref name="placeholder"/> wherever a
  /// module references it.</summary>
  /// <param name="placeholder">The entity type a module declared as a placeholder.</param>
  /// <param name="replacement">The entity type this application uses in its place; it derives from
  /// <paramref name="placeholder"/>.</param>
  /// <remarks>Recorded here and validated by the model builder; must be called before <see cref="Init"/>.</remarks>
  public void ReplaceEntity(Type placeholder, Type replacement) {
    ArgumentNullException.ThrowIfNull(placeholder);
    ArgumentNullException.ThrowIfNull(replacement);
    RefuseAfterInit(nameof(ReplaceEntity));
    _entityReplacements.Add(new ReplacementDeclaration(placeholder, replacement));
  }

  /// <summary>Declares that <typeparamref name="TReplacement"/> stands for <typeparamref name="TPlaceholder"/>
  /// wherever a module references it.</summary>
  /// <typeparam name="TPlaceholder">The entity type a module declared as a placeholder.</typeparam>
  /// <typeparam name="TReplacement">The entity type this application uses in its place.</typeparam>
  public void ReplaceEntity<TPlaceholder, TReplacement>() where TReplacement : TPlaceholder =>
    ReplaceEntity(typeof(TPlaceholder), typeof(TReplacement));

  /// <summary>Gets or sets how long a cached block is trusted where <see cref="SetCached"/> gives no expiration.
  /// </summary>
  /// <value>The expiration of a cached block. The default is one minute. <see cref="Timeout.InfiniteTimeSpan"/> keeps
  /// a block until this application writes the entity's rows.</value>
  public TimeSpan DefaultCacheExpiration { get; set; } = TimeSpan.FromSeconds(60);

  /// <summary>Declares that the given entities are held whole in memory, and for how long a block of one is
  /// trusted.</summary>
  /// <param name="cacheType">A bitwise combination of the enumeration values that specifies where the rows are held.
  /// </param>
  /// <param name="entityTypes">An array that contains the entity types to cache.</param>
  /// <param name="expiration">How long a block is trusted; after it, the block is refilled on the next read, whatever
  /// wrote the rows meanwhile. <see langword="null"/> for <see cref="DefaultCacheExpiration"/>;
  /// <see cref="Timeout.InfiniteTimeSpan"/> for a block that lives until this application writes the rows.</param>
  /// <remarks>Must be called before <see cref="Init"/>. A block is dropped when this application writes the entity's
  /// rows, by a save or a statement, and refilled after its expiration whatever wrote them.</remarks>
  public void SetCached(CacheType cacheType, Type[] entityTypes, TimeSpan? expiration = null) {
    ArgumentNullException.ThrowIfNull(entityTypes);
    RefuseAfterInit(nameof(SetCached));
    if (expiration is { } given && given <= TimeSpan.Zero && given != Timeout.InfiniteTimeSpan)
      throw new ArgumentOutOfRangeException(nameof(expiration),
        "A cache expiration is a positive span, Timeout.InfiniteTimeSpan for none, or null for the application's default.");
    foreach (var type in entityTypes) {
      ArgumentNullException.ThrowIfNull(type);
      _cachedEntities.Add(new CacheDeclaration(cacheType, type, expiration));
    }
  }

  /// <summary>Adds a cross-cutting rule, such as multi-tenancy or soft delete, to this application.</summary>
  /// <param name="extension">The extension to add.</param>
  /// <remarks>Must be called before <see cref="Init"/>, which calls the extensions in the order they were added once
  /// the model is built; an extension is the only way to attach a filter to an entity.</remarks>
  public void AddExtension(AppExtension extension) {
    ArgumentNullException.ThrowIfNull(extension);
    RefuseAfterInit(nameof(AddExtension));
    if (extension.App != null)
      throw new InvalidOperationException(
        $"Extension '{extension.GetType().Name}' already belongs to application '{extension.App.Name}'.");
    extension.App = this;
    _extensions.Add(extension);
  }

  /// <summary>Registers a service under its interface, replacing any registered before.</summary>
  /// <typeparam name="TService">The interface the service is registered under.</typeparam>
  /// <param name="service">The service instance.</param>
  public void RegisterService<TService>(TService service) where TService : class {
    ArgumentNullException.ThrowIfNull(service);
    _services[typeof(TService)] = service;
  }

  /// <summary>Finds a service among the application's own, then in <see cref="HostServices"/>.</summary>
  /// <typeparam name="TService">The interface the service is registered under.</typeparam>
  /// <returns>The service, or <see langword="null"/> if neither has one under the interface.</returns>
  public TService? FindService<TService>() where TService : class =>
    FindOwnService<TService>() ?? HostServices?.GetService(typeof(TService)) as TService;

  /// <summary>Gets a service from the application's own, then from <see cref="HostServices"/>.</summary>
  /// <typeparam name="TService">The interface the service is registered under.</typeparam>
  /// <returns>The service.</returns>
  /// <exception cref="InvalidOperationException">Neither has a service under the interface.</exception>
  public TService GetService<TService>() where TService : class =>
    FindService<TService>()
      ?? throw new InvalidOperationException(
        $"Application '{Name}': no service is registered for '{typeof(TService).Name}'.");

  /// <summary>Writes this application's log to a file and sets the log mode to <see cref="LogMode.Details"/>.</summary>
  /// <param name="path">The path of the log file.</param>
  /// <returns>The listener that writes the file.</returns>
  /// <remarks>An existing file at <paramref name="path"/> is deleted. The file is not rotated or size-capped. An
  /// application that wants detail only on error sets <see cref="EntityAppSettings.LogMode"/> after this call.
  /// </remarks>
  public FileLogListener SetLogFilePath(string path) {
    ArgumentException.ThrowIfNullOrWhiteSpace(path);
    if (File.Exists(path))
      File.Delete(path);

    var listener = new FileLogListener(path);
    LogService.AddListener(listener);
    Settings.LogMode = LogMode.Details;
    return listener;
  }

  /// <summary>Called once the model is built without errors, to hook what this application listens to.</summary>
  /// <remarks>Tells the modules first, then the extensions in the order they were added, then the services that
  /// implement <see cref="IServiceInit"/>; the order within the modules and within the services is undefined. An
  /// override must call the base implementation.</remarks>
  protected virtual void OnModelConstructed() {
    foreach (var module in _modules)
      module.OnModelConstructed(Model!);
    foreach (var extension in _extensions)
      extension.OnModelConstructed(Model!);
    foreach (var service in _services.Values.OfType<IServiceInit>().Distinct())
      service.Init(this);
  }

  /// <summary>Gets the replacements recorded by <c>ReplaceEntity</c>, for the model builder to validate and apply.
  /// </summary>
  internal IReadOnlyList<ReplacementDeclaration> EntityReplacements => _entityReplacements;

  /// <summary>Finds a service registered with this application, without asking <see cref="HostServices"/>.
  /// </summary>
  /// <typeparam name="TService">The interface the service is registered under.</typeparam>
  /// <returns>The service, or <see langword="null"/> if none is registered under the interface.</returns>
  internal TService? FindOwnService<TService>() where TService : class =>
    _services.TryGetValue(typeof(TService), out var service) ? (TService)service : null;

  /// <summary>Raises <see cref="OnModelConstructed"/> on behalf of the startup helper.</summary>
  internal void AnnounceModelConstructed() => OnModelConstructed();

  /// <summary>Throws if the model is already built, since a declaration made now would never be applied.</summary>
  private void RefuseAfterInit(string what) {
    if (Model != null)
      throw new InvalidOperationException(
        $"Application '{Name}': {what} is a startup declaration and the model is already built — it has to be said "
        + "before Init().");
  }

  /// <summary>Represents an entity declared as cached, held until the model exists to carry the flag.</summary>
  public sealed record CacheDeclaration(CacheType CacheType, Type EntityType, TimeSpan? Expiration = null);

  /// <summary>Represents a placeholder entity type and its replacement, held until the model exists to validate
  /// them.</summary>
  public sealed record ReplacementDeclaration(Type Placeholder, Type Replacement);
}
