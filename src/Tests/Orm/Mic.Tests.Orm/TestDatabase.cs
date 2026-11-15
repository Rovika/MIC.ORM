using Mic.Orm;
using Mic.Orm.Db;
using Mic.Orm.Db.Model;
using Mic.Orm.Db.Postgres;
using Mic.Orm.Db.Sqlite;

namespace Mic.Tests.Orm;

/// <summary>THE database, for the tests that have exactly one.</summary>
/// <remarks>An application registers its databases by name and looks them up the same way — there is deliberately
/// no "the one" on <see cref="EntityApp"/>, because an application with two tenants has no such thing and a property
/// answering anyway would be the old single-database assumption wearing a new name.
///
/// A test is the case where there IS one: each of these builds one application, maps it for one provider, and then
/// asks about the model that came back. Saying that here, once, beats a service lookup in every assertion.</remarks>
static class TestDatabase {
  public static DbModel MappedModel(this EntityApp app) =>
    app.DataAccess.FindDatabase()?.DbModel
      ?? throw new InvalidOperationException(
        $"Application '{app.Name}' has no database — CreateDatabase() did not run, or did not survive.");

  public static bool HasDatabase(this EntityApp app) =>
    app.DataAccess.FindDatabase() != null;

  /// <summary>Maps an initialized application to Postgres without connecting, and returns the db model: the model the
  /// query engine translates against in the tests that never reach a server.</summary>
  public static DbModel MapToPostgres(this EntityApp app) {
    AppInitHelper.CreateDatabase(app, new DbSettings(new PostgresProvider(),
      "Host=nowhere.invalid;Database=none;Username=none;Password=none"));
    return app.MappedModel();
  }

  /// <summary>A database for an application whose tests never reach one — a session requires it, and this suite is
  /// about what a session does WITHOUT a server: the save set, validation, events, stubs, the map.</summary>
  /// <remarks>SQLite in memory, mapped and never connected: `Init()` has run, the model maps, and a statement that
  /// did reach it would find no tables — which is what a test here asserting on a read has to expect.</remarks>
  public static EntityApp WithSqlite(this EntityApp app) {
    if (app.Initialized)
      AppInitHelper.CreateDatabase(app, new DbSettings(new SqliteProvider(), "Data Source=:memory:"));
    return app;
  }
}
