using Mic.Orm.Db.Model.Upgrades;
using Mic.Orm.Db.Provider;

namespace Mic.Orm.Db;

/// <summary>Holds what is needed to reach one database instance: the provider, the connection strings and the
/// policies that govern connections to it.</summary>
/// <remarks>One <see cref="DbProvider"/> describes a kind of server and may serve any number of databases, each with
/// its own settings.</remarks>
public class DbSettings {
  readonly string? _schemaConnectionString;

  public DbSettings(DbProvider provider, string connectionString, string? schemaConnectionString = null,
      string databaseAlias = DefaultDatabaseAlias) {
    ArgumentNullException.ThrowIfNull(provider);
    ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
    Provider = provider;
    ConnectionString = connectionString;
    _schemaConnectionString = schemaConnectionString;
    DatabaseAlias = databaseAlias;
  }

  public const string DefaultDatabaseAlias = "(default)";

  public DbProvider Provider { get; }

  /// <summary>Gets the name the application knows this database by, which a session selects it by where several are
  /// registered.</summary>
  public string DatabaseAlias { get; }

  /// <summary>Gets the connection string the application's own work runs on.</summary>
  public string ConnectionString { get; }

  /// <summary>Gets the connection string schema changes run on, typically a higher-privileged login.</summary>
  /// <remarks>When no schema connection string was supplied, this is <see cref="ConnectionString"/>.</remarks>
  public string SchemaConnectionString => _schemaConnectionString ?? ConnectionString;

  /// <summary>Gets a value that indicates whether the schema connection string differs from the application's.
  /// </summary>
  public bool HasSeparateSchemaConnection =>
    _schemaConnectionString != null && _schemaConnectionString != ConnectionString;

  /// <summary>Gets or sets how the model is mapped to this database: what the schema enforces and how the tables
  /// are named.</summary>
  /// <value>A combination of the enumeration values. The default is <see cref="DbOptions.Default"/>.</value>
  /// <remarks>Read when the application connects. A value replaces the whole set:
  /// <c>Options = DbOptions.Default | DbOptions.AddSchemaToTableNames</c>.</remarks>
  public DbOptions Options { get; set; } = DbOptions.Default;

  /// <summary>Gets or sets how far the application may go in changing the database's schema.</summary>
  /// <value>One of the enumeration values that specifies the upgrade mode. The default is
  /// <see cref="DbUpgradeMode.NonProductionOnly"/>, which leaves a database marked as production alone.</value>
  public DbUpgradeMode UpgradeMode { get; set; } = DbUpgradeMode.NonProductionOnly;

  /// <summary>Gets or sets a value that indicates whether an upgrade drops tables the model does not know about.
  /// </summary>
  /// <value><see langword="true"/> to drop unknown tables; otherwise, <see langword="false"/>. The default is
  /// <see langword="false"/>.</value>
  public bool DropUnknownTables { get; set; }

  /// <summary>Gets or sets the patterns of the database objects an upgrade neither drops nor changes.</summary>
  /// <value>The patterns. Each is compared with an object's path as the upgrade report prints it, such as
  /// <c>sales.Order.IX_Report_Month</c>, ignoring case; <c>*</c> stands for any run of characters. The default is
  /// an empty list.</value>
  /// <remarks>A table's path is <c>schema.Table</c>, as is a view's, a function's and a sequence's; a column's, a
  /// key's, an index's and a constraint's is <c>schema.Table.Name</c>. An addition is never withheld. Nothing is
  /// reported about an object left alone.</remarks>
  public IList<string> IgnoreDbObjects { get; set; } = [];

  /// <summary>Gets or sets how long an upgrade waits for another instance to finish upgrading before giving up.
  /// </summary>
  /// <value>The wait. The default is 60 seconds.</value>
  /// <remarks>Giving up is an error, not a skipped upgrade.</remarks>
  public TimeSpan UpgradeLockWait { get; set; } = TimeSpan.FromSeconds(60);

  /// <summary>Gets or sets the name of the table MIC keeps its own record of the database in.</summary>
  /// <value>The table name. The default is <c>DbInfo</c>.</value>
  /// <remarks>The table is found by this name, without an entity model, so a database with no application behind it
  /// can be read too.</remarks>
  public string DbInfoTableName { get; set; } = "DbInfo";

  /// <summary>Gets or sets the length of a parameter's rows, as the JSON text the statement would bind, past which
  /// the rows are staged in a temp table the statement reads instead.</summary>
  /// <value>The length in characters. The default is 500,000, about 10,000 Guid keys.</value>
  /// <remarks>A temp table costs a create, a fill and a drop around the statement, and measured slower than the
  /// bound value at every length on PostgreSQL, MySQL and SQLite. SQL Server and Oracle fill the table over their
  /// bulk channel: there the table is faster from about 10,000 keys on SQL Server and as fast from about 5,000 on
  /// Oracle.</remarks>
  public int RowsTempTableThreshold { get; set; } = 500_000;

  /// <summary>Gets or sets the path the upgrade report is written to.</summary>
  /// <value>The path, or <see langword="null"/> to write no report.</value>
  public string? UpgradeReportPath { get; set; }

  public override string ToString() => $"{DatabaseAlias} ({Provider.ServerKind})";
}
