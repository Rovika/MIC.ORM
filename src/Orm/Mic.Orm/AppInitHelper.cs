using Mic.Orm.Db;
using Mic.Orm.Db.Model;
using Mic.Orm.Db.Model.Construction;
using Mic.Orm.Db.Model.Upgrades;
using Mic.Orm.DbInfo;
using Mic.Orm.Logging;
using Mic.Orm.Model;
using Mic.Orm.Model.Construction;

namespace Mic.Orm;

/// <summary>Runs the startup phases of an <see cref="EntityApp"/>: building its model, mapping a database and
/// connecting to one.</summary>
/// <remarks><see cref="Initialize"/> reports a broken model by its return value and the build report;
/// <see cref="ConnectToAsync"/> throws when it cannot connect.</remarks>
internal static class AppInitHelper {

  /// <summary>Builds the model from an application's declarations and announces it to the modules, extensions and
  /// services.</summary>
  /// <param name="app">The application to initialize.</param>
  /// <returns>The build report.</returns>
  /// <remarks>The model is kept on the application whether or not it built cleanly, so that a failed one can be
  /// inspected; <see cref="EntityApp.Initialized"/> says whether anything may run on it. The extensions see the
  /// entities once the model is on the application, since a filter they attach binds against it. An exception from
  /// an application hook is logged as a startup error and ends the pass.</remarks>
  /// <exception cref="InvalidOperationException">The model has errors, or a hook threw.</exception>
  internal static EntityModelBuildReport Initialize(EntityApp app) {
    if (app.Settings.IncludeDbInfoModule && app.FindModule(DbInfoModule.ModuleName) == null)
      app.AddModule(new DbInfoModule());

    var builder = new ModelBuilder();
    app.Model = builder.Build(app);
    if (app.Model.BuildReport.HasErrors || !builder.ApplyExtensions(app))
      throw BuildModelException(app);

    var hooksRan = true;
    try {
      app.AnnounceModelConstructed();
    } catch (Exception ex) {
      LogStartupError(app, $"'{app.GetType().Name}.OnModelConstructed' threw: {ex.Message}", ex);
      hooksRan = false;
    }

    // Set last, so a hook reached during startup does not find an application already claiming to be initialised.
    app.Initialized = !app.Model.BuildReport.HasErrors && hooksRan;
    if (!app.Initialized)
      throw BuildModelException(app);
    return app.Model.BuildReport;
  }

  private static InvalidOperationException BuildModelException(EntityApp app) =>
    new($"Application '{app.Name}': the model did not build." + Environment.NewLine + app.Model!.BuildReport.Format());

  /// <summary>Asynchronously connects an application to a database and brings the database schema into line with
  /// the model.</summary>
  /// <param name="app">The initialized application.</param>
  /// <param name="settings">The settings of the database to connect to.</param>
  /// <param name="cancellation">A token that cancels the operation.</param>
  /// <returns>A task that represents the asynchronous operation.</returns>
  /// <remarks>Connectivity is verified before the upgrade. A schema that differs from the model without permission to
  /// change it is reported, not thrown.</remarks>
  /// <exception cref="InvalidOperationException">The application did not initialize.</exception>
  /// <exception cref="DataAccessException">The model cannot be mapped to the provider, or the upgrade failed.
  /// </exception>
  internal static async Task<DbUpgradeReport> ConnectToAsync(EntityApp app, DbSettings settings, CancellationToken cancellation) {
    if (!app.Initialized)
      throw BuildNotInitializedException(app);

    // Mapped first: a model this provider cannot express fails before a connection is attempted.
    if (CreateDatabase(app, settings, out var mappingReport) is not { } database)
      throw new DataAccessException(
        $"Application '{app.Name}': the model cannot be mapped to {settings.Provider.ServerKind}. " + mappingReport)
        { DatabaseAlias = settings.DatabaseAlias };

    // Opened before the connectivity check, which bypasses the executor and would otherwise log nothing on failure.
    using var operation = new OperationContext(app) { CorrelationId = $"connect:{database.Alias}" };

    try {
      await database.CheckConnectivityAsync(cancellation);
    } catch (Exception ex) {
      operation.LogError($"connecting to '{database.Alias}' failed before the upgrade: {ex.Message}", ex);
      throw;
    }

    app.DatabaseEvents.OnUpgrading(database);

    // The report is taken in the finally block, so a run that threw still leaves its report on the database.
    var manager = new DbUpgradeManager(database) { Context = operation };
    try {
      var upgrade = await manager.UpgradeAsync(cancellation);

      // Set before the handlers run, so one reading the report through the application finds it.
      database.UpgradeReport = upgrade;
      app.DatabaseEvents.OnUpgraded(database, upgrade);

      if (upgrade.HasErrors)
        throw new DataAccessException(
          $"Application '{app.Name}': the upgrade of database '{database.Alias}' failed."
          + Environment.NewLine + upgrade.Report()) { DatabaseAlias = database.Alias };

      // Set before the event, so a handler meets an application that already says it is connected.
      app.IsConnected = true;
      app.DatabaseEvents.OnConnected(database);
      return upgrade;
    } catch (Exception ex) {
      // Logged here because not every failure ran a statement: an upgrade lock that times out never reaches a command.
      operation.LogError($"connecting to '{database.Alias}' failed: {ex.Message}", ex);
      throw;
    } finally {
      if (manager.Report is { } report)
        database.UpgradeReport = report;
    }
  }

  /// <summary>Maps the model for a provider and registers the result as a database, without touching the server.
  /// </summary>
  /// <param name="app">The initialized application.</param>
  /// <param name="settings">The settings of the database, including its provider.</param>
  /// <returns>The registered database, or <see langword="null"/> if the application is not initialized or the model
  /// cannot be mapped to the provider.</returns>
  /// <remarks>A failed mapping is not registered; its build report goes to the application's log. A database
  /// registered earlier for the same kind of provider and the same <see cref="DbSettings.Options"/> shares its
  /// <see cref="DbModel"/> with the new one; the model is read-only once built and holds nothing of one database.
  /// </remarks>
  internal static Database? CreateDatabase(EntityApp app, DbSettings settings) => CreateDatabase(app, settings, out _);

  /// <summary>Maps the application's model to a provider and registers the database, telling why where it cannot.
  /// </summary>
  /// <param name="app">The initialized application.</param>
  /// <param name="settings">The settings of the database, including its provider.</param>
  /// <param name="failure">When this method returns, the formatted build report of a mapping that failed, or the
  /// reason the application is not ready; <see langword="null"/> if the database is registered.</param>
  /// <returns>The registered database, or <see langword="null"/> if the application is not initialized or the model
  /// cannot be mapped to the provider.</returns>
  internal static Database? CreateDatabase(EntityApp app, DbSettings settings, out string? failure) {
    ArgumentNullException.ThrowIfNull(settings);
    failure = null;
    if (!app.Initialized) {
      failure = "Init() must run, and succeed, before a database can be created.";
      LogStartupError(app, failure);
      return null;
    }

    var dbModel = FindSharedDbModel(app, settings) ?? new DbModelBuilder(app, app.Model!, settings).Build();
    if (dbModel.BuildReport.HasErrors) {
      failure = dbModel.BuildReport.Format();
      app.LogService.Post([
        .. dbModel.BuildReport.Messages,
        new ErrorLogEntry(default, app.TimeService.UtcNow,
          $"the model cannot be mapped to {settings.Provider.ServerKind}.", null),
      ]);
      return null;
    }

    var database = new Database(dbModel, settings) {
      GuidScheme = app.Settings.GuidScheme == GuidScheme.Auto ? settings.Provider.GuidScheme : app.Settings.GuidScheme,
    };
    app.DataAccess.RegisterDatabase(database);
    return database;
  }

  /// <summary>Finds the db model of a registered database that the new settings would map identically.</summary>
  /// <param name="app">The initialized application.</param>
  /// <param name="settings">The settings of the database being created.</param>
  /// <returns>The db model to share, or <see langword="null"/> if no registered database matches.</returns>
  /// <remarks>A db model is a function of the entity model, the provider's type and the mapping options; two
  /// databases of one application that agree on the three get the same tables and SQL.</remarks>
  private static DbModel? FindSharedDbModel(EntityApp app, DbSettings settings) {
    var providerType = settings.Provider.GetType();
    foreach (var registered in app.DataAccess.Databases) {
      var dbModel = registered.DbModel;
      if (dbModel.EntityModel == app.Model && dbModel.Options == settings.Options
          && registered.Provider.GetType() == providerType)
        return dbModel;
    }
    return null;
  }

  /// <summary>Logs a startup fault and keeps the first one on the application.</summary>
  private static void LogStartupError(EntityApp app, string message, Exception? error = null) {
    app.StartupError ??= (message, error);
    app.LogService.Post([new ErrorLogEntry(default, app.TimeService.UtcNow, message, error)]);
  }

  /// <summary>Builds the exception that says why an application that did not initialize cannot connect.</summary>
  private static InvalidOperationException BuildNotInitializedException(EntityApp app) {
    var reason = app.StartupError?.Message
      ?? (app.Model == null ? "Init() has not been run." : app.Model.BuildReport.Format());
    return new InvalidOperationException(
      $"Application '{app.Name}' cannot connect because it did not initialise: {reason}",
      app.StartupError?.Error);
  }
}
