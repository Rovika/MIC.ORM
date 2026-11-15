using Mic.Orm;
using Mic.Orm.Db;
using Mic.Orm.Db.Model.Upgrades;
using Mic.Orm.Db.Postgres;
using Npgsql;

namespace BeautyShop;

/// <summary>Builds the sample database from nothing: create, upgrade, seed, refresh the materialized view, cluster.
/// </summary>
/// <remarks>ONE COMMAND, BECAUSE DOING IT BY HAND COST TWICE. The sample lived only in a container's anonymous volume,
/// and when that volume went so did the database — twice rebuilt through a throwaway test that was written, run and
/// deleted each time. A sample nobody can recreate on demand is not a sample, it is a fixture somebody is nursing.
///
/// <code>dotnet run --project src/Samples/BeautyShop</code>
///
/// IT IS IDEMPOTENT ONLY IN ITS FIRST STEP. Creating the database is skipped when it is there, but the upgrade and
/// the seed run again — so a second run against a seeded database ADDS a second set of orgs. Pass `--drop` to start
/// from nothing, which is what a rebuild means.</remarks>
public static class BeautySetup {
  public static async Task<int> Main(string[] args) {
    var drop = args.Contains("--drop", StringComparer.OrdinalIgnoreCase);
    var connectionString = args.FirstOrDefault(a => a.Contains('=') && !a.StartsWith("--"))
      ?? BeautyShopApp.DefaultConnectionString;
    try {
      await BuildAsync(connectionString, drop, Console.WriteLine);
      return 0;
    } catch (Exception ex) {
      Console.Error.WriteLine(ex.Message);
      return 1;
    }
  }

  /// <summary>Create the database if it is missing, map the model onto it, fill it, and finish it.</summary>
  /// <param name="dropFirst">Drop the database first — a REBUILD rather than a top-up.</param>
  public static async Task BuildAsync(string connectionString, bool dropFirst = false, Action<string>? report = null) {
    ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
    report ??= _ => { };

    var databaseName = new NpgsqlConnectionStringBuilder(connectionString).Database
      ?? throw new ArgumentException("The connection string names no database.", nameof(connectionString));

    await EnsureDatabaseAsync(connectionString, databaseName, dropFirst, report);

    // ----- the schema, from the entity model: the tables, the views and the function -----------------------------
    await using var app = new BeautyShopApp();
    app.Init();

    var settings = new DbSettings(new PostgresProvider(), connectionString) { UpgradeMode = DbUpgradeMode.Always };
    await app.ConnectToAsync(settings);

    var database = app.DataAccess.FindDatabase(settings.DatabaseAlias)!;
    report($"schema '{BeautyShopApp.SchemaName}' is up to date");

    // ----- the data ------------------------------------------------------------------------------------------------
    report($"seeded: {await BeautySeed.RunAsync(app)}");

    // The materialized view was created empty with the schema, before there were rows to hold.
    await app.OpenSession().RefreshMaterializedViewAsync<ILowStock>();
    report("refreshed mv_LowStock");

    // Last, because it rewrites tables and wants them full: a reorder done before the seed would order nothing.
    var clustered = await PgHelper.ApplyClusteringAsync(database);
    report($"clustered {clustered.Count} tables");
  }

  private static async Task EnsureDatabaseAsync(string connectionString, string databaseName, bool dropFirst,
      Action<string> report) {
    // A database cannot be created from a connection TO it, so this one goes to `postgres` — the maintenance database
    // every server has.
    var maintenance = new NpgsqlConnectionStringBuilder(connectionString) { Database = "postgres" }.ConnectionString;
    await using var connection = new NpgsqlConnection(maintenance);
    await connection.OpenAsync();

    if (dropFirst) {
      // WITH (FORCE) terminates whatever is still connected. Without it a single idle session — a browser tab holding
      // the schema, a psql left open — refuses the drop.
      await ExecuteAsync(connection, $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)");
      report($"dropped '{databaseName}'");
    }

    await using var exists = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @name", connection);
    exists.Parameters.AddWithValue("name", databaseName);
    if (await exists.ExecuteScalarAsync() != null) {
      report($"'{databaseName}' is already there");
      return;
    }
    await ExecuteAsync(connection, $"CREATE DATABASE \"{databaseName}\"");
    report($"created '{databaseName}'");
  }

  private static async Task ExecuteAsync(NpgsqlConnection connection, string sql) {
    await using var command = new NpgsqlCommand(sql, connection);
    await command.ExecuteNonQueryAsync();
  }
}
