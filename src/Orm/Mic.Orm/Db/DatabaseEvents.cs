using Mic.Orm.Db.Model.Upgrades;

namespace Mic.Orm.Db;

public delegate void DatabaseEventHandler(Database database);

/// <summary>Represents the method that handles a request for a database no registration matches.</summary>
/// <remarks>A handler may register the database; the registry looks it up again after the handlers have run.</remarks>
public delegate void DatabaseMissingHandler(EntityApp app, string name);

public delegate void DbUpgradeEventHandler(Database database, DbUpgradeReport report);

/// <summary>Provides the events an application's databases raise while connecting, upgrading and being looked up.
/// </summary>
/// <remarks>These events are raised with no session in hand, at startup or when a tenant's database arrives.</remarks>
public class DatabaseEvents {
  /// <summary>Occurs when a database has been reached, its connectivity verified and its schema upgrade completed.
  /// </summary>
  public event DatabaseEventHandler? Connected;

  /// <summary>Occurs before a database is compared with the model and possibly changed.</summary>
  public event DatabaseEventHandler? Upgrading;

  /// <summary>Occurs when an upgrade run has finished, with the report of what differed and what was applied.</summary>
  /// <remarks>A run that throws does not raise this event; the database's own report still holds what it got to.
  /// </remarks>
  public event DbUpgradeEventHandler? Upgraded;

  /// <summary>Occurs when an operation asks for a database by a name nothing is registered under.</summary>
  /// <remarks>The registry looks the name up again after the handlers have run, so a handler that registers the
  /// database turns the failure into a hit. A handler that does nothing leaves the failure as it was.</remarks>
  public event DatabaseMissingHandler? DatabaseMissing;

  internal void OnConnected(Database database) => Connected?.Invoke(database);
  internal void OnUpgrading(Database database) => Upgrading?.Invoke(database);
  internal void OnUpgraded(Database database, DbUpgradeReport report) => Upgraded?.Invoke(database, report);
  internal void OnDatabaseMissing(EntityApp app, string name) => DatabaseMissing?.Invoke(app, name);
}
