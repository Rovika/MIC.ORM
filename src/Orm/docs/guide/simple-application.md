# Simple application

One program with all the parts of MIC ORM: an entity is an interface, a module declares entities, an
application holds modules and connects to a database, a session reads and writes.

```csharp
using Mic.Linq;
using Mic.Orm;
using Mic.Orm.Db;
using Mic.Orm.Db.Postgres;

// Define data model - entities representing database tables

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

// EntityModule - functional group of entities, and EntityApp - collection of modules

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

public static class Program {
  public static ShopApp ShopApp = null!;

  public static async Task Main() {
    // Create an entity app instance, save it global singleton.
    ShopApp = new ShopApp();
    ShopApp.Init();

    // Connect the app to the database
    await ShopApp.ConnectToAsync(new DbSettings(new PostgresProvider(),
      "Host=localhost;Database=shop;Username=shop;Password=shop"));

    // Data operations - create a customer. Open an EntitySession, create new entity and save it
    var session = ShopApp.OpenSession();
    var customer = session.NewEntity<ICustomer>();
    customer.Name = "Ada";
    var order = session.NewEntity<IOrder>();
    order.Customer = customer;
    order.PlacedOn = DateTime.UtcNow;
    order.Total = 12.50m;
    await session.SaveChangesAsync();

    // Query the customers table and find the customer we just created
    var ada = await session.EntitySet<ICustomer>()
      .Where(c => c.Name == "Ada")
      .FirstAsync();
    Console.WriteLine($"{ada.Name} has {ada.Orders.Count} order(s)");
  }
}
```

The first run of this program creates the schema `shop` with two tables, a foreign key and a unique index. A
later run with a property added to `ICustomer` adds the column and changes nothing else.

Each part has its page: [Application data model](data-model.md), [Application startup](startup.md),
[Core runtime objects](core-objects.md), [Reading data](linq-queries.md), [Updating data](saving-changes.md).
[Getting started](getting-started.md) creates a program like this one from the template, in one command.

A larger example is [Beauty Shop](beauty-shop.md): a multi-tenant application of thirty entities with views, a
table function and a seeder.
