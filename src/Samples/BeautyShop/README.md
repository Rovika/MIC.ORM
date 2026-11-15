# Beauty Shop

The sample application of MIC ORM. Beauty Shop is a multi-tenant online service for the beauty industry. A SaaS
company hosts it and sells it to individual beauty companies: nail salons, lash studios, hair salons. Each company is
a company with its own shops, staff, customers, appointments, inventory, sales and payroll, and a tenant of the
service. Every row belongs to one tenant; the tenant is infrastructure, the company is the business record.

It is a model with a seeder, three views, a table function and a console entry point. There is no user interface.

## What is in it

| File | Content |
|---|---|
| `BeautyEntities.cs` | 32 entity interfaces, the ORM's `ITenant` beside them, three view entities, the rows of the table function. |
| `Enums.cs` | The enums. |
| `BeautyShopApp.cs` | `BeautyShopModule`, which registers the entities, defines the views in LINQ and the table function in SQL; `BeautyShopApp`, which adds the module in schema `beauty` and the multi-tenancy extension. |
| `BeautySetup.cs` | `Main`: creates the database, connects, seeds, refreshes the materialized view, clusters the tables. |
| `BeautySeed.cs`, `BeautySeedData.cs` | The seeder: five tenants, a company each, from a fixed random seed. |

The guide's page on it: `src/Orm/docs/guide/beauty-shop.md`.

## As a demo

The project is a console application. Run it as it is:

```bash
docker compose up -d postgres
dotnet run --project src/Samples/BeautyShop -- --drop
```

The first command starts the repository's test PostgreSQL on port 5433. The second creates the database
`BeautyShop` on it, connects the application (the upgrade creates the schema, the tables, the views and the
function), seeds five companies, refreshes `mv_LowStock` and clusters the tables. `--drop` deletes the database first; a
run without it adds a second set of companies. Any argument containing `=` is taken as the connection string; the default
is `BeautyShopApp.DefaultConnectionString`.

Then query it from code, from `psql`, or through the `mic-db` tool, whose sample configs under
`src/Tools/Mic.DbTool/Samples` point at this database.

## As the model of a real application

The project is also a library. A web application references it and gets the application, its module and its
entity interfaces; `Main` is only the setup and a host never calls it.

```csharp
using BeautyShop;
using Mic.Orm;
using Mic.Orm.Db;
using Mic.Orm.Db.Postgres;

await using var app = new BeautyShopApp();
app.Init();
await app.ConnectToAsync(new DbSettings(new PostgresProvider(), ConnectionStringHelper.Resolve("env:BEAUTY_CONN")));

var context = new OperationContext(app);
context.SetValue("TenantId", tenantId);
var session = context.OpenSession();
var low = await session.EntitySet<ILowStock>().ToListAsync();   // the tenant's rows, the extension's doing
```

The guide's [Startup](../../Orm/docs/guide/startup.md) page shows the ASP.NET Core registration: the application
as a singleton, an `OperationContext` per request with the tenant id in its values.

## As a test harness

`Mic.Tests.Orm` references this project. Six of its test classes build the model, five of them as
`PlainBeautyShopApp`, their own application over `BeautyShopModule` without the multi-tenancy extension, so that
the statements they compare carry no tenant condition; they map it to PostgreSQL without a server and translate
queries against the db model. `BeautyShopTests` builds the real `BeautyShopApp` and asserts the model's own shape
and the extension's filters. The
model is used for this because it is the realistic one: thirty-two tables, a self-reference, link entities with and without payload, nullable references, a filtered
index. A change to an entity here changes what those tests translate against; a
renamed member they mention breaks them.

No server-backed suite uses it. The DML, LINQ and DDL suites have their own models and never touch the `BeautyShop`
database.
