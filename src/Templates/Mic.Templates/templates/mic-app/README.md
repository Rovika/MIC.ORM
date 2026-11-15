# Shop

A MIC application, made from the `mic-app` template. Three projects:

| project | what it is |
|---|---|
| `Shop.Model` | The entity model: the interfaces, the module that declares them, the application class. References `Mic.Orm` alone and knows no server. |
| `Shop` | The console host: it chooses the server (`DatabaseHelper`), reads where the database is (`appsettings.json`), connects and runs. The database tool's configs are in `mic-db/`. |
| `Shop.Tests` | One MSTest test that connects the way the host does, saves a customer with an order and reads them back. |

## Running

The host reads `appsettings.json` beside it. The connection string there is the value itself, `env:NAME` for an
environment variable, or `file:path` for a file holding it, the path relative to the settings file. As generated it
is `env:SHOP_CONN`, so set that variable (on SQLite the file `shop.db` is used instead, created in the working
directory):

```bash
dotnet run --project Shop
dotnet test
```

The first connect creates the schema and the tables; every connect after brings the database up to the model, unless
the database declares itself production. The model's schema is named in `ShopApp.SchemaName`. A server without
schemas (SQLite) ignores it; on Oracle a schema is a user, so connect as that user or have it created first.

## The database tool

`mic-db` is the MIC database tool, installed with `dotnet tool install --global Mic.DbTool`. The two configs in
`Shop/mic-db/` point at the host's output folder, so build the host first.

* `mic-db upgrade Shop/mic-db/upgrade.json` writes the script that brings the database up to the model, for a
  production upgrade done by hand; `--apply` runs it.
* `mic-db entities Shop/mic-db/entities.json` writes the entity model of an existing database as C# source beside
  the config, to review and move into `Shop.Model` in place of the starter entities.

Both read the connection string the same way the host does.

## The model

`ICustomer` and `IOrder` are a starter. An entity is an interface and MIC emits the class; a reference to another
entity is a property of its type; a getter-only `IList<T>` is the one-to-many back from it; attributes carry sizes,
keys, indexes and precision. Add an entity to the module's `RegisterEntities` call and the next connect adds its
table.
