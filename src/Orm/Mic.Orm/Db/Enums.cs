
namespace Mic.Orm.Db;

/// <summary>Specifies how an application's model is mapped to one database: what the schema enforces and how the
/// tables are named.</summary>
/// <remarks>The flags are read when the application connects to the database and not afterwards. A value replaces
/// the whole set: a database that adds a flag writes <c>DbOptions.Default | DbOptions.AddSchemaToTableNames</c>.
/// </remarks>
[Flags]
public enum DbOptions {
  None = 0,

  /// <summary>The model's references are foreign-key constraints in the database.</summary>
  /// <remarks>Without it MIC creates no foreign-key constraint and leaves those the database has alone, for a
  /// database whose integrity is kept by the applications.</remarks>
  UseRefIntegrity = 1,

  /// <summary>Every foreign key gets an index.</summary>
  /// <remarks>Ignored where the server indexes foreign keys itself, as InnoDB does.</remarks>
  AutoIndexForeignKeys = 1 << 1,

  /// <summary>A table is named by the plural of its entity name: <c>Employees</c> for <c>IEmployee</c>.</summary>
  /// <remarks><c>[Entity(PluralName = …)]</c> gives the plural where the rule gets it wrong, and
  /// <c>[Entity(TableName = …)]</c> is taken as it is. A view keeps its name.</remarks>
  PluralizeTableNames = 1 << 4,

  /// <summary>On a server without schemas, the schema is prefixed to the table name: <c>hr_Employee</c>.</summary>
  /// <remarks>For SQLite, where the schemas of a large model collapse into one namespace. The model builder warns
  /// when the collapse would risk a collision.</remarks>
  AddSchemaToTableNames = 1 << 5,

  /// <summary>The default: <see cref="UseRefIntegrity"/>.</summary>
  Default = UseRefIntegrity,
}

public enum DbExecutionType { NonQuery, Scalar, Reader }

/// <summary>Specifies how long a connection stays open.</summary>
/// <remarks>Of the lifetimes asked for on one connection, the highest wins.</remarks>
public enum DbConnectionLifetime {
  /// <summary>The end of the current request, unless a transaction is open on the connection.</summary>
  Operation,
  /// <summary>An explicit close, as at the end of the session or of a web request.</summary>
  Explicit,
}

[Flags]
public enum DbConnectionFlags {
  None = 0,
  /// <summary>A transaction committed when the session saves.</summary>
  CommitOnSave = 1,
}

/// <summary>Specifies the kind of work a request is.</summary>
/// <remarks>Schema work is not a request; <see cref="DdlExecutor"/> runs it with no session.</remarks>
public enum DataRequestKind {
  /// <summary>A read.</summary>
  Query,
  /// <summary>A write: inserts, updates and deletes.</summary>
  Mutation,
  /// <summary>A statement the application wrote, run as given, which may read or write.</summary>
  Sql,
}

