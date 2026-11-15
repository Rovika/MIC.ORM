# MIC ORM

MIC ORM is a full-featured .NET ORM, with an extended LINQ engine and other modern ORM features. It matches
modern ORMs like EF Core feature for feature, and also provides advanced capabilities.

It is the direct successor of [VITA](https://github.com/rivantsov/vita), by the same author. It is a complete
rewrite that keeps VITA's main design choices and stays backward compatible in most elements of application
design: VITA entity definitions work in MIC ORM without changes.
[Migrating from VITA](https://rovika.github.io/Mic.Orm/guide/migrating-from-vita.html) has the steps.

Supported database servers: PostgreSQL, SQL Server, MySQL, Oracle and SQLite.

See more in the documentation: <https://rovika.github.io/Mic.Orm/>

> **Preview.** The current version is `0.9.0-preview.1`. The API may change before 1.0.

## Application code example

```csharp
using Mic.Linq;
using Mic.Orm;
using Mic.Orm.Db;
using Mic.Orm.Db.Postgres;

[Entity]
public interface ICustomer {
  [PrimaryKey, Auto] Guid Id { get; set; }
  [Size(100), Unique] string Name { get; set; }
  IList<IOrder> Orders { get; }
}

[Entity]
public interface IOrder {
  [PrimaryKey, Auto] Guid Id { get; set; }
  ICustomer Customer { get; set; }
  [Utc] DateTime PlacedOn { get; set; }
  [Column(Precision = 12, Scale = 2)] decimal Total { get; set; }
}

public class ShopModule : EntityModule {
  public ShopModule() : base("Shop", new Version(1, 0)) {
    RegisterEntities(typeof(ICustomer), typeof(IOrder));
  }
}

public class ShopApp : EntityApp {
  public ShopApp() : base("Shop", new Version(1, 0), new EntityAppSettings { DefaultSchema = "shop" }) {
    AddModule(new ShopModule(), "shop");
  }
}

var app = new ShopApp();
app.Init();
await app.ConnectToAsync(new DbSettings(new PostgresProvider(),
  "Host=localhost;Database=shop;Username=shop;Password=shop"));

var session = app.OpenSession();
var customer = session.NewEntity<ICustomer>();
customer.Name = "Ada";
var order = session.NewEntity<IOrder>();
order.Customer = customer;
order.PlacedOn = DateTime.UtcNow;
order.Total = 12.50m;
await session.SaveChangesAsync();

var ada = await session.EntitySet<ICustomer>().Where(c => c.Name == "Ada").FirstAsync();
Console.WriteLine($"{ada.Name} has {ada.Orders.Count} order(s)");
```

Database objects (tables, keys, indexes) are created automatically on the first application start.

## Quick start

The preview packages are published to a feed of their own, not to nuget.org. Add the feed once:

```bash
dotnet nuget add source https://rovika.github.io/Mic.Orm/feed/index.json --name mic-orm
```

On a machine that uses package source mapping, also map `Mic.*` to this source.

Install the project template and the `mic-db` tool:

```bash
dotnet new install Mic.Templates
```

```bash
dotnet tool install --global Mic.DbTool --prerelease
```

Create an application skeleton: the entity model, a console host and a test project.

```bash
dotnet new mic-app -n Shop --provider Postgres
```

`--provider` is `Postgres`, `MySql`, `MsSql`, `Oracle` or `SQLite`. The solution is made in a new folder
`Shop`. The host reads its connection string from the environment variable `SHOP_CONN`. Set it and run:

```bash
cd Shop
export SHOP_CONN="Host=localhost;Database=shop;Username=shop;Password=shop"
dotnet run --project Shop
```

The run creates the schema `shop` and its tables, adds a customer with an order and lists the customers. With
`SQLite` the host uses the file `shop.db` and no variable is needed.

To use the ORM in an existing project, add the provider package for your server; it brings `Mic.Orm` with it.

```bash
dotnet add package Mic.Orm.Db.Postgres --prerelease
```

| Package | Holds |
|---|---|
| `Mic.Orm` | The ORM. A project that holds only the entity model references this one alone. |
| `Mic.Orm.Db.Postgres`, `.SqlServer`, `.MySql`, `.Oracle`, `.Sqlite` | The provider for one server. |
| `Mic.DbTool` | The `mic-db` tool: the upgrade script for a production database, and entities from an existing one. |
| `Mic.Templates` | The `mic-app` project template. |

[Getting started](https://rovika.github.io/Mic.Orm/guide/getting-started.html) goes through the same steps in
detail.

## License

MIT. See [LICENSE](LICENSE).
