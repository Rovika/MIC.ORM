namespace Mic.Orm;

// The default implementations are in Mic.Orm.Services; every service takes the application, so one can reach another.

/// <summary>Defines the optional hook a registered service implements to be told when the model is built.</summary>
/// <remarks>Services are registered before a model exists, so a service that keys anything by entity type or hooks
/// <see cref="Model.EntityTypeInfo.Events"/> does it here, at the same moment as
/// <see cref="EntityModule.OnModelConstructed"/>. The model is read and hooked, not changed.</remarks>
public interface IServiceInit {
  /// <summary>Hooks what the service listens to, once the model is built without errors.</summary>
  /// <param name="app">The application whose model is built.</param>
  /// <remarks>Called once, during <see cref="EntityApp.Init"/>.</remarks>
  void Init(EntityApp app);
}

/// <summary>Provides the current time as the application sees it.</summary>
/// <remarks>Everything that stamps a row goes through this service, so a test can move the application's clock and
/// have the data agree with it.</remarks>
public interface ITimeService {
  DateTime UtcNow { get; }
}

/// <summary>Provides the Guids for members declared <c>[Auto(AutoType.NewGuid)]</c>, and for session and transaction
/// ids.</summary>
/// <remarks>A session asks for the scheme of its database, <see cref="Db.Database.GuidScheme"/>, so one factory
/// serves every database of the application.</remarks>
public interface IGuidFactory {
  /// <summary>Creates a new Guid in a layout, stamped with the given instant or with now.</summary>
  /// <param name="scheme">The layout; not <see cref="GuidScheme.Auto"/>, which a database resolves.</param>
  /// <param name="timestamp">The instant to stamp the Guid with, or <see langword="default"/> for now; ignored by
  /// <see cref="GuidScheme.V4"/>.</param>
  /// <param name="sequence">The place of the Guid among those made at the instant, counted from 0, which the
  /// server orders them by after the time; or <see langword="null"/> for a Guid with no place among others, whose
  /// bits after the time are random. Ignored by <see cref="GuidScheme.V4"/>.</param>
  /// <returns>The new Guid.</returns>
  /// <remarks>A session mints every Guid of one save with the instant its <c>CreatedOn</c> stamps use, and counts
  /// them, so the keys of entities created before one save sort in the order the entities were created. The count
  /// takes 12 bits: past 4,096 the time in the Guid is a millisecond ahead for every 4,096. Two sessions that
  /// create in the same millisecond are not ordered against each other.</remarks>
  Guid NewGuid(GuidScheme scheme, DateTime timestamp = default, int? sequence = null);
}

/// <summary>Defines the registry of the databases an application can reach, and resolves the one an operation is
/// for.</summary>
/// <remarks>Resolution is by <see cref="OperationContext.DatabaseAlias"/>, where <see langword="null"/> means the
/// default. A session resolves its database once, when it opens, and keeps it for its life. When a database is not
/// registered, <see cref="GetDatabase"/> raises <c>EntityApp.DatabaseEvents.DatabaseMissing</c> and looks again, so a
/// handler that registers a tenant's database turns the miss into a hit.</remarks>
public interface IDataAccessService {
  /// <summary>Gets the database an operation is for.</summary>
  /// <param name="context">The operation.</param>
  /// <returns>The database.</returns>
  /// <exception cref="InvalidOperationException">No database is registered under the operation's alias.</exception>
  Db.Database GetDatabase(OperationContext context);

  /// <summary>Finds a registered database by name.</summary>
  /// <param name="name">The database alias, or <see langword="null"/> for the default database.</param>
  /// <returns>The database, or <see langword="null"/> if none is registered under the name.</returns>
  Db.Database? FindDatabase(string? name = null);

  IReadOnlyList<Db.Database> Databases { get; }

  void RegisterDatabase(Db.Database database);
  bool RemoveDatabase(string name);
}
