# Guid generation

A row needs its primary key before anything can refer to it. An identity key is made by the server when the
insert runs, so a new order and its lines cannot be tied together by key until then. A Guid key is made in the
application. MIC ORM generates one for every `Guid` member marked `[Auto]`, at the moment the entity is created,
and makes it ordered by time. This page covers what a Guid key gives, the layouts, the time a key carries, the
order of the keys of one save, and the settings.

## Guid keys and identity keys

```csharp
public interface IOrder {
  [PrimaryKey, Auto] Guid Id { get; set; }
  IList<IOrderLine> Lines { get; }
}

var order = session.NewEntity<IOrder>();       // order.Id is set
var line = session.NewEntity<IOrderLine>();
line.Order = order;
await session.SaveChangesAsync();
```

* The key exists as soon as the entity does. A reference between two new entities is a key value from the
  start, and the save writes the rows as they are.
* The save reads nothing back. An identity key is made by the server, and each insert has to return it.
* A Guid is unique across databases. Rows are copied between two databases, or between a test and a production
  database, with their keys.

An identity key is four or eight bytes where a Guid is sixteen, and it reads easily. `[Identity]` on an `int` or
a `long` key is there for a table where that counts; see [The primary key](entity-members.md#the-primary-key).

`[Auto]` generates a value for any `Guid` member, not only for a key.

## Guid versions

A Guid carries a version number, which says how its bits were made. Three versions matter here, named as the
`GuidScheme` values:

| Scheme | Layout |
|---|---|
| `V4` | Version 4: random throughout. `Guid.NewGuid()` makes these, and VITA generated its keys so. |
| `V7` | Version 7: the Unix time in milliseconds in the first six bytes, random after. |
| `V8SqlServer` | Version 8, a layout of the application's own: the same millisecond in the last six bytes, random before. |

MIC ORM generates Guids ordered by time, `V7` or `V8SqlServer`. A random key falls anywhere in the key's index,
so the rows inserted in one minute are scattered over the whole of it. A key ordered by time is greater than
every key made before it: new rows go to the end of the index, next to each other, as with an identity key.

## The time in a key

A Guid ordered by time carries the time it was made, to the millisecond. `EntityHelper.GetTimestamp` reads it:

```csharp
DateTime? createdOn = EntityHelper.GetTimestamp(order.Id);    // UTC; null for a random Guid
```

The time is the session's. Every entity created between two saves of a session gets one instant, taken when the
first of them is created, and the `[Auto(AutoType.CreatedOn)]` members of that save are stamped with the same
instant. The key of an entity is therefore also its created-on stamp.

Choose `V4` for a key that must not show when its row was made.

## The order within one save

Entities created before one save share an instant, and their keys still sort in the order the entities were
created. The session numbers the keys it generates, and the number sits in the key right after the time. A batch
of log records created in a loop and saved once comes back from `OrderBy(r => r.Id)` in the order of the loop.

The number takes twelve bits. From the 4,097th entity of one save on, the time in the key is a millisecond ahead
of the instant, and one more millisecond for every further 4,096; the order holds for a batch of any size.

Two sessions that create entities in the same millisecond are not ordered against each other.

## SQL Server

SQL Server orders a `uniqueidentifier` by its last six bytes first. The other four servers compare a Guid from
its first byte. A version 7 Guid is therefore in time order everywhere except on SQL Server. For it the ORM has
the `V8SqlServer` layout, with the millisecond in those last six bytes: in time order to SQL Server, and random
to any other reader. `EntityHelper.GetTimestamp` reads both layouts.

## Settings

The scheme is taken per database, from the database's provider: `V7` on PostgreSQL, MySQL, Oracle and SQLite,
`V8SqlServer` on SQL Server. An application with a database on each server uses each scheme where it belongs.
To set it:

```csharp
// For every database of the application: in the application's settings.
public ShopApp() : base("Shop", new Version(1, 0), new EntityAppSettings { GuidScheme = GuidScheme.V4 }) { }

// For one database: in its provider.
var dbSettings = new DbSettings(new SqlServerProvider(guidScheme: GuidScheme.V4), connectionString);
```

A database that VITA made holds random keys. Under the default, new rows get keys ordered by time beside them;
set `V4` to go on with random ones.

The scheme is permanent in effect. Changing it does not rewrite stored keys, so a table then holds a mix, and
the time cannot be read from a key whose layout is unknown. The scheme in force is written into the `DbInfo` row
by every upgrade that applied DDL, and a connect under a different scheme is a warning in the upgrade report.
