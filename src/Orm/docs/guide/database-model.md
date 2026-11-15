# Maintaining the database schema

A database and the code over it change together through an application's life: a column added, a table
renamed, an index for a new query. Each change has to reach every database the application runs on, the
developer's own, the test database, staging, production, and the code has to find the schema it expects on each.

MIC ORM keeps the two matched in either of two ways: scripts written by a tool and run by hand, or an upgrade
the application applies at connect from the C# model. This page describes both, the connect sequence, and the
`DbInfo` table, in which the ORM engine tracks the schema version of the database and the outcome of the last
upgrade.

## Two ways to keep a database and its code matched

**By hand, with scripts.** The traditional way. The schema is created by a script, and every change to it is
another script, a migration, numbered and run against each database in order: the developer's own, the shared
test database, staging, production. The code and the migrations are two sources of truth, kept in step by
discipline, and a database whose history is unknown cannot be brought to a version with confidence.

MIC ORM supports this way. A production database is upgraded by a script, written by the `mic-db` tool from the
difference between the model and the database, reviewed and run by whoever owns production. The application
itself, told `DbUpgradeMode.Never`, does not read the schema: it connects and is ready for queries. See
[Generating the upgrade script](tool-upgrade.md).

**From the code.** The way MIC ORM is designed for. The C# model declares everything: entities, members, keys,
indexes, references, sequences, views. The database schema is derived from the model, and when the application
connects it reads the live database, compares the two and applies the difference. There is nothing to write
beside the model. The model is in source control, and every database converges on it from its current state: a
developer's local database, the shared test database, a fresh one.

A failure half-way does not leave the database in a broken state. The next connect compares again and produces
exactly the work that remains, and says so:

```
The last upgrade of this database FAILED part-way. This run re-diffs against what is actually there, so it
produces exactly the work that remains.
```

## The connect sequence

```csharp
using Mic.Orm;
using Mic.Orm.Db;
using Mic.Orm.Db.Model.Upgrades;
using Mic.Orm.Db.Postgres;

var app = new ShopApp();
app.Init();
var report = await app.ConnectToAsync(new DbSettings(new PostgresProvider(), connectionString) {
  UpgradeMode = DbUpgradeMode.NonProductionOnly,
});
if (!report.IsApplied)
  Console.WriteLine(report.Report());    // the differences the mode did not let the connect apply
```

`Init` builds the entity model from the modules. `ConnectToAsync` then, for the database the settings name:

1. Derives the database model the entity model implies for this server: the tables, columns and their types,
   keys, indexes, foreign keys, sequences, views, table functions.
2. Reads the live database: its schemas, tables, columns, keys, indexes and the rest, and the `DbInfo` row MIC
   keeps in it.
3. Compares the two and lists the differences.
4. Decides whether it may apply them, from `UpgradeMode` and what the database declares itself to be.
5. Takes an upgrade lock on the server, so that two instances starting together do not apply the same change,
   and re-reads the database if it had to wait. On Oracle the lock is `DBMS_LOCK`, which needs `GRANT EXECUTE ON
   DBMS_LOCK` to the application's user; without it the upgrade warns and runs uncoordinated.
6. Runs the upgrade, statement by statement, in a fixed order of stages.
7. Writes the `DbInfo` row: the application's name and version, the module versions, the Guid scheme the keys
   are minted under, and whether the upgrade succeeded.

The result is the `DbUpgradeReport` the connect returns, also kept on the database as `UpgradeReport`: the
differences found, the script built, whether it was applied, and every message. An upgrade that ran and failed
throws; one the mode withheld is reported, with `IsApplied` false.

## The upgrade's changes

The comparison is of objects, by name. The upgrade applies the differences in a fixed order: dependent objects
dropped first, then renames, additions and alterations, then the keys, constraints, views and functions over
the finished tables, and the destructive drops of columns, tables, sequences and schemas last.

* **A new entity** is a new table with its columns, primary key, indexes and foreign keys.
* **A new member** is a new column. A required column added to a table with rows is created nullable, filled
  with a placeholder value for the type, and then made not null.
* **A changed type, size or default** is an `ALTER` of the column.
* **A rename** is a rename, not a drop and a create, when the entity or member carries `[OldNames("…")]` naming
  what it was called. Without it, a renamed member is a dropped column and a new one.
* **A new, changed or removed index, key or foreign key** is dropped and created as needed.
* **A view or table function** whose definition changed is dropped and recreated. MIC stores a hash of the
  definition with the object, so the comparison is of what was declared, not of the text the server gives back.
* **A member or entity removed from the model** is a dropped column or table, applied in the last stages.
  Tables the model knows nothing about are left alone, and not reported, unless `DbSettings.DropUnknownTables`
  is set.

A column marked `[AsIs]` is never altered: the upgrade leaves its type and definition as the database has them.

A database often holds an object the model does not declare on a table the model does: an index a DBA added for
a report, a column another system writes. The upgrade drops it as it drops what a removed member leaves behind.
`DbSettings.IgnoreDbObjects` names the objects the upgrade is to leave alone:

```csharp
settings.IgnoreDbObjects = ["*.Order.IX_Report_*", "sales.Order.LegacyCode", "*.dba_*"];
```

Each entry is a pattern over an object's path, compared ignoring case; `*` stands for any run of characters. The
path is the one the upgrade report prints: `schema.Table` for a table, a view, a function and a sequence,
`schema.Table.Name` for a column, an index, a key and a constraint. A pattern that starts with `*` fits every
schema, and a server without schemas. A matching object is neither dropped nor changed to the model's
definition, and the report says nothing of it: to the upgrade the object does not exist. An object the model
declares and the database lacks is still created, and a rename the model declares with `[OldNames]` is applied.

The same holds where the upgrade recreates a column. The script drops and adds the model's own indexes over the
column and leaves an ignored one to the server. Where the server will not drop a column an index still stands
on, the upgrade fails with the server's message.

```csharp
[Entity, OldNames("Client")]
public interface ICustomer {
  [PrimaryKey, Auto] Guid Id { get; set; }
  [Size(100), OldNames("FullName")] string Name { get; set; }
  [AsIs] string? LegacyCode { get; set; }
}
```

## Development, staging, production

Every database MIC has connected to carries one row in its `DbInfo` table. One column of it, `InstanceType`, is
set by an administrator in the database and never written by MIC: `Development`, `Staging` or `Production`. It
is the database's own statement of what it is, and it is paired with the application's `UpgradeMode`:

| `UpgradeMode` | Development | Staging | Production |
|---|---|---|---|
| `Always` | upgraded | upgraded | upgraded |
| `NonProductionOnly` (default) | upgraded | upgraded | reported, not changed |
| `Never` | not read | not read | not read |

Under `Never` the connect checks that the server answers and reads nothing: no comparison, no lock, no `DbInfo`
row read or written. It is the mode for a production host that starts many instances, where an administrator
keeps the schema. The differences between the model and the database are then shown by `mic-db upgrade`, which
writes the script and applies nothing.

A database with no `DbInfo` row has never been managed by MIC, and the application's mode alone decides. A
database at a version ahead of the application's, that declares itself staging or production, refuses the
connect; an older build must not downgrade a newer schema. The check reads the `DbInfo` row, so it does not run
under `Never`.

A typical deployment: developers run with the default; the staging database upgrades itself when a build is
deployed to it; production is marked `Production` once and is upgraded by the script `mic-db upgrade` writes from
the same comparison. [Generating the upgrade script](tool-upgrade.md) says how.

## The DbInfo table

The ORM keeps its record of a database in a table of that database, `DbInfo`, through a module of its own,
`DbInfoModule`, added to the application by default. The table holds one row: the application's name and
version, the versions of its modules, whether the last upgrade succeeded, and the `InstanceType` the
administrator set. The connect reads the row before it compares, and writes it after an upgrade it applied. A
connect that applies nothing writes the row only where the database matches the model and the row is missing or
behind: a new version that changes no schema, a database a script created, a failed upgrade repaired by hand. It
leaves the time of the last upgrade as it is. If the login may not write the row, the report carries a warning
and the connect stands. A version of the application or a module is the one declared in its constructor; MIC
compares versions, and a database ahead of the application is refused as above. `DbSettings.DbInfoTableName`
names the table.

The table is optional. A database another team or tool controls, where nothing may be added, runs with two
settings: `EntityAppSettings.IncludeDbInfoModule = false` leaves the module out, so no table is created and
nothing is written, and `DbSettings.UpgradeMode = DbUpgradeMode.Never` keeps the connect from reading or
changing the schema.

## The schema connection

Schema changes need a privileged connection, with elevated permissions the application should not run with.
`DbSettings` takes a second connection string for them:

```csharp
var settings = new DbSettings(new PostgresProvider(),
  connectionString: "Host=db;Database=shop;Username=shop_app;Password=…",
  schemaConnectionString: "Host=db;Database=shop;Username=shop_owner;Password=…");
```

The upgrade runs on the schema connection; everything else on the application's. The second string is optional:
without it, the upgrade runs on the application's connection.

