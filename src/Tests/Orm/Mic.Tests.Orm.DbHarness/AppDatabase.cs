using Mic.Orm;
using Mic.Orm.Db;
using Mic.Orm.Db.Model;

namespace Mic.Tests.Orm.Db;

/// <summary>THE database, for the tests that have exactly one.</summary>
/// <remarks>An application registers its databases by name and looks them up the same way — there is deliberately
/// no "the one" on <see cref="EntityApp"/>, because an application with two tenants has no such thing and a property
/// answering anyway would be the old single-database assumption wearing a new name.
///
/// A test is the case where there IS one: each of these builds an application against this test's own isolated
/// database and asks about the model that came back.</remarks>
public static class AppDatabase {
  /// <summary>Map the model for this database and register it, WITHOUT connecting — and hand back the db model.
  /// </summary>
  /// <remarks>`AppInitHelper.CreateDatabase` is internal: an application CONNECTS, it does not map, so the door stays
  /// shut in the public API and open exactly where the tests need it. What they need it for is a db model built
  /// against a provider that is never reached — a DDL script to read, a mapping to assert — which no application ever
  /// wants.</remarks>
  public static DbModel MapTo(this EntityApp app, DbSettings settings) {
    AppInitHelper.CreateDatabase(app, settings);
    return app.MappedModel();
  }

  public static DbModel MappedModel(this EntityApp app) =>
    app.DataAccess.FindDatabase()?.DbModel
      ?? throw new InvalidOperationException(
        $"Application '{app.Name}' has no database — CreateDatabase() did not run, or did not survive.");
}
