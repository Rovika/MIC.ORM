# Application data model

## Entities

An ORM is an engine the application builds its data access layer on, the bridge to the database. To build it
the engine needs two things: the structure of the database, the tables with their columns and keys, and the
shape the application wants a row returned in, for each table. These shapes are the entities, the in-memory
images of a table's rows. An entity's members are the values it holds and its links to other entities. The
application declares every entity and maps it to its table; together they are the data model the ORM holds in
memory, from which it builds the SQL and maps the query results to C# objects.

*Entity* and *member* are the general ORM terms, and this guide uses them throughout. In MIC ORM an entity is a
C# interface, and its members are the interface's properties.

Most ORMs declare an entity as a POCO, a plain class with a property per column. In MIC ORM the interface
states the contract of a row, the typed members, and the class behind it carries the mechanics: the storage,
the change tracking, the references loaded on first read. The application writes no class and maintains no
mapping; the implementing class is emitted at startup from the interface and its attributes, and the session
hands out its instances. See [Entities are interfaces](highlights.md#entities-are-interfaces).

```csharp
using Mic.Orm;

[Entity]
public interface ICustomer {
  [PrimaryKey, Auto] Guid Id { get; set; }
  [Size(100), Unique] string Name { get; set; }
  [Size(200)] string? Email { get; set; }
  CustomerStatus Status { get; set; }
  [Utc] DateTime? LastOrderOn { get; set; }
}

public enum CustomerStatus { Active, Suspended, Closed }
```

The interface is marked `[Entity]`, and each property it declares is a member of the entity. A scalar member
maps to a table column of the best-matching type, with attributes for what the type does not say: `[Size]` for
a string's length, `[Utc]` for a date kept in UTC. Nullability comes from the C# type: `string?` is a nullable
column, `string` is not. The types and the sizes, the primary key, the references and the lists, and the
members that are more than a column are on [Entity members and types](entity-members.md); the attributes are
listed on [C# attributes for modeling](attributes.md).

The entity's name is the interface name without its leading `I`: `ICustomer` is the entity `Customer`, and by
default its table is `Customer`. `[Entity(Name = …, TableName = …)]` overrides either. A model that follows
another naming convention throughout replaces `EntityAppSettings.NamingPolicy`, the class that derives every
entity, table, column and key name.

Entity interfaces inherit. The members of a base interface become members of every entity that extends it,
which is how a key, a tenant reference or an audit pair is declared once for a whole model; the base interface
itself is not an entity and has no table.

## Modules and the application

A real-world model has hundreds of tables, and a model of hundreds of entities in one list is not readable or
maintainable. The model is therefore built in two layers. An `EntityModule` declares a set of entities, one
functional area: customers and orders, billing, the catalog. An `EntityApp` is the application: the modules it
is made of, the schema each goes in, and the settings and extensions that apply to all of them. The application
class is part of the data model, not startup code; it is what the host instantiates.

```csharp
using Mic.Orm;

public class ShopModule : EntityModule {
  public ShopModule() : base("Shop", new Version(1, 0)) {
    RegisterEntities(typeof(ICustomer), typeof(IOrder), typeof(IOrderLine), typeof(IProduct));
    RegisterSequence("OrderNumbers", start: 1000, increment: 1);
  }
}

public class ShopApp : EntityApp {
  public ShopApp() : base("Shop", new Version(1, 0), new EntityAppSettings { DefaultSchema = "shop" }) {
    AddModule(new ShopModule(), "shop");
    AddModule(new BillingModule(), "billing");
  }
}
```

A module's name and version are recorded in the database at each upgrade. Beside its entities, a module
registers the other database objects it owns, sequences, views and table functions, see [Database
objects](database-objects.md), and the derived types its JSON members may hold, see [JSON](json.md). A module
may use another module's entities in its own; `Requires<T>()` records the dependency, and a missing module is an
error in the build report. The application adds the modules and assigns the schemas: `AddModule(module, schema)`
adds one, `AssignSchema(schema, modules)` puts several in one schema, and a module without a schema goes in the
application's `DefaultSchema`. Each module can live in its own project; a large application is several of them.

Once the application class is defined, the host instantiates it, builds the model and connects it to a
database: see [Application startup](startup.md). The database schema is derived from the model and kept up to
date with it: see [Maintaining the database schema](database-model.md).

## The data model is database-agnostic

The modules and the application reference `Mic.Orm` only. The provider for the server is constructed by the
host at startup and passed to `ConnectToAsync` in `DbSettings`, so the same application assembly runs on
PostgreSQL, SQL Server, MySQL, Oracle and SQLite. The template `mic-app` puts the model in its own project for
this reason: `Shop.Model` references `Mic.Orm`, and only the host `Shop` references `Mic.Orm.Db.Postgres`.

Server-specific declarations exist where a feature needs them: a view or a table function written in SQL takes
one text per `DbServerKind`, and `[SqlExpression(DbServerKind.Postgres, "…")]` gives a computed column's
expression per server. An application that supports one server writes one text.

## Database first scenario

If you start with an existing database of many tables, you can generate the entity definitions for it with the
`mic-db entities` tool. The command reads the database's tables and views and writes the entity interfaces
and the modules, every database name pinned in an attribute so the C# names can be chosen freely. See
[Generating entities from a database](tool-entities.md). From then on the interfaces are the model, and the
database is maintained from them like any other.
