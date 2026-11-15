using System.Collections.Concurrent;
using Mic.Orm.Db;

namespace Mic.Orm.Services;

/// <summary>Provides the default <see cref="IDataAccessService"/>: a registry of databases, resolved by name.</summary>
/// <remarks>Safe for concurrent use, since it is shared by every session in the process. Nothing creates a database
/// on demand; an application whose tenants are not known at startup handles <c>DatabaseEvents.DatabaseMissing</c> or
/// replaces this service.</remarks>
public class DataAccessService(EntityApp app) : IDataAccessService {
  protected EntityApp App { get; } = app;

  readonly ConcurrentDictionary<string, Database> _databases = new(StringComparer.OrdinalIgnoreCase);
  readonly object _missLock = new();

  public IReadOnlyList<Database> Databases => _databases.Values.ToArray();

  public Database? FindDatabase(string? name = null) =>
    _databases.TryGetValue(name ?? DbSettings.DefaultDatabaseAlias, out var source) ? source : null;

  /// <summary>Gets the database an operation is for.</summary>
  /// <param name="context">The operation.</param>
  /// <returns>The database.</returns>
  /// <remarks>When no database is registered under the operation's alias, raises <c>DatabaseEvents.DatabaseMissing</c>
  /// and looks again, so a handler that registers the database turns the miss into a hit. One miss is handled at a
  /// time: a second operation naming the same alias waits for the first's handler and finds the database it
  /// registered. A hit takes no lock.</remarks>
  /// <exception cref="InvalidOperationException">No database is registered under the operation's alias.</exception>
  public Database GetDatabase(OperationContext context) {
    ArgumentNullException.ThrowIfNull(context);
    var name = context.DatabaseAlias ?? DbSettings.DefaultDatabaseAlias;
    if (FindDatabase(name) is { } database)
      return database;

    lock (_missLock) {
      if (FindDatabase(name) is { } registered)
        return registered;

      App.DatabaseEvents.OnDatabaseMissing(App, name);

      return FindDatabase(name)
        ?? throw new InvalidOperationException(
          $"Application '{App.Name}': no database named '{name}'. "
          + (_databases.IsEmpty
            ? "Nothing is registered — EntityApp.ConnectToAsync() must run before anything can be read or written."
            : $"Registered: {string.Join(", ", _databases.Keys.Order())}."));
    }
  }

  public void RegisterDatabase(Database database) {
    ArgumentNullException.ThrowIfNull(database);
    _databases[database.Alias] = database;
  }

  public bool RemoveDatabase(string name) => _databases.TryRemove(name, out _);
}
