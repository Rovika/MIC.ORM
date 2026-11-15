# Advanced example - Beauty Shop

Beauty Shop is the advanced example application of MIC ORM, after [Simple application](simple-application.md): a
multi-tenant online service for the beauty industry. A SaaS company hosts it and sells it to individual beauty
companies: nail salons, lash studios, hair salons. Each company is a tenant of the service, with its own shops,
staff, customers, appointments, inventory, sales and payroll, and every row belongs to one tenant. It is a model
with three views, a table function and a seeder, not a user interface. Its source is in the repository under
`src/Samples/BeautyShop`; the project's `README.md` says how it is run as a demo, referenced by a host, and used by
the tests.

## Contents

| File | Content |
|---|---|
| `BeautyEntities.cs` | 32 entity interfaces, three view entities and the rows of the table function. |
| `Enums.cs` | The enums: `RecordStatus`, `UserType`, `EmployeeRoles`, `Specialties`, `AppointmentStatus`, `PaymentMethod`, `SaleStatus`, `PayrollStatus` and others. |
| `BeautyShopApp.cs` | `BeautyShopModule`, which registers the entities, the views and the table function, and `BeautyShopApp`, which adds the module in schema `beauty` and the extension. |
| `BeautySetup.cs` | A console entry point: creates the database, connects (which creates the schema), seeds it, refreshes the materialized view, clusters the tables. `--drop` rebuilds from nothing. |
| `BeautySeed.cs`, `BeautySeedData.cs` | The seeder: five tenants, a company each, with shops, staff, customers, a diary of appointments, sales and payroll, from a fixed random seed. |
| `README.md` | The project's own account: the demo run, the reference from a host, the use by the tests. |

## The model

The tenant and the company are two things. The tenant is infrastructure: the ORM's `ITenant`, a number every row
carries, which the [multi-tenancy extension](multi-tenancy.md) filters by and stamps. The company is business
domain: the record of the account, with its name, its status and its contacts. Every entity inherits
`IBeautyEntity`, which declares the key and, through the shipped `ITenantBound`, the tenant; only the top entities
of the hierarchy reference `ICompany`, and a child reached through its parent does not:

```csharp
public interface IBeautyEntity : ITenantBound {
  [PrimaryKey, Auto] Guid Id { get; set; }
}

[Entity]
public interface ICompany : IBeautyEntity {
  [Size(100), Unique] string Name { get; set; }
  RecordStatus Status { get; set; }
}

[Entity, Unique("Tenant,Name")]
public interface IShop : IBeautyEntity {
  [Index] ICompany Company { get; set; }
  [Size(100)] string Name { get; set; }
  IAddress? Address { get; set; }
  IPhone? Phone { get; set; }
  [Size(60)] string TimeZone { get; set; }
  RecordStatus Status { get; set; }

  IList<IShopHours> OpenHours { get; }
  [OneToMany("HomeShop")] IList<IEmployee> Employees { get; }
  [ManyToMany(typeof(IShopEmployee))] IList<IEmployee> Staff { get; }
}
```

The entities, grouped:

| Group | Entities |
|---|---|
| Tenancy, the company and sign-in | `ITenant`, `ICompany`, `IUser` |
| People and places | `IAddress`, `IPhone`, `IPerson`, `IShop`, `IShopHours`, `IEmployee`, `IShopEmployee`, `ICustomer`, `IAvailability` |
| Services | `IServiceCategory`, `IService`, `IServiceStation` |
| Appointments | `IAppointment`, `IAppointmentService` |
| Inventory | `IVendor`, `IProductCategory`, `IProduct`, `IStockLevel`, `IShipment`, `IShipmentLine` |
| Money | `IPaymentSource`, `IVendorInvoice`, `IVendorPayment`, `ISale`, `ISaleLine`, `IPayment` |
| Payroll | `ITimeEntry`, `IPayrollRun`, `IPayrollLine` |

Features the model exercises: a self-referencing reference (`IEmployee.Manager`), one-to-many lists with no
attribute, link entities with payload (`IAppointmentService`, `ISaleLine`) and without (`IShopEmployee`, behind
the `[ManyToMany]` lists `IEmployee.Shops` and `IShop.Staff`), nullable references, `DateOnly` and `TimeOnly`
members, decimals with explicit precision, plain and `[Flags]` enums, two-column unique keys, a composite primary
key over two references (`IShopEmployee`), a filtered index, `[Unlimited]` notes, `[Secret]` on a password hash,
`[Utc]` timestamps, three views defined in LINQ, one of them materialized, and a table function with a PostgreSQL
body.

A member of staff has a nullable `HomeShop`, where the diary looks for them first, and a list of `Shops` they may
be booked at; a company contact, the owner or the accountant, may have no shop at all. The contacts are the
employees whose `Roles` carry `EmployeeRoles.CompanyContact`; the person behind the employee holds the email and
the phone. `IUser.Company` is nullable, for an administrator who belongs to no company; `IEmployee.User` and
`ICustomer.User` are nullable references to it.

## The application

```csharp
public class BeautyShopModule : EntityModule {
  public const string ModuleName = "BeautyShop";

  public BeautyShopModule() : base(ModuleName, new Version(1, 0)) {
    RegisterEntities(
      typeof(ITenant), typeof(ICompany), typeof(IUser),
      typeof(IAddress), typeof(IPhone), typeof(IPerson),
      typeof(IShop), typeof(IShopHours), typeof(IEmployee), typeof(IShopEmployee), typeof(ICustomer),
      typeof(IAvailability),
      typeof(IServiceCategory), typeof(IService), typeof(IServiceStation),
      typeof(IAppointment), typeof(IAppointmentService),
      typeof(IVendor), typeof(IProductCategory), typeof(IProduct), typeof(IStockLevel),
      typeof(IShipment), typeof(IShipmentLine),
      typeof(IPaymentSource), typeof(IVendorInvoice), typeof(IVendorPayment),
      typeof(ISale), typeof(ISaleLine), typeof(IPayment),
      typeof(ITimeEntry), typeof(IPayrollRun), typeof(IPayrollLine));
  }
}

public class BeautyShopApp : EntityApp {
  public const string SchemaName = "beauty";
  public const string DefaultConnectionString =
    "Host=localhost;Port=5433;Database=BeautyShop;Username=mic;Password=mic";

  public BeautyShopApp() : base("BeautyShop", new Version(1, 0),
      new EntityAppSettings { DefaultSchema = SchemaName }) {
    AddModule(new BeautyShopModule(), SchemaName);
    AddExtension(new MultiTenantExtension());
  }
}
```

One module holds the whole model, the ORM's `ITenant` registered with the application's entities. `DefaultSchema`
is set so that the ORM's `DbInfo` table lands in `beauty` with the application's tables. The translation tests,
whose expected statements carry no tenant condition, build the module into an application of their own without
the extension; the sample has no switch for it.

## Multi-tenancy

The service is multi-tenant: every row carries its tenant, and an operation run for one tenant must read and
write that tenant's rows and no other's. The sample adds the shipped [multi-tenancy extension](multi-tenancy.md)
with nothing to configure: every entity inherits `ITenantBound`, and the extension works on the reference's key
column, `Tenant_Id`, comparing it with the context's tenant on every read and writing the context's tenant into
it at save.

```csharp
// In the application's constructor:
AddExtension(new MultiTenantExtension());

// Per request, the host names the tenant of the signed-in user, and opens the session from that context:
var context = new OperationContext(app, user);
context.SetValue("TenantId", tenantId);
using var session = context.OpenSession();
```

> **Important.** In a real application a session is always opened from the operation context,
> `context.OpenSession()`. The context is the request's: it holds the user's identity, the values the request
> carries, the tenant among them, and whatever else the operation needs to know, and the session reads them from
> there: the tenant filter and the stamp, the `CreatedBy` kind of audit values, the user for a fault. The guide's
> samples open sessions with `app.OpenSession()` for brevity, on a context that names nothing; in this sample
> such a session sees no rows of a bound entity and has a save of one refused.

A view is one database object for every tenant: its select is defined without the extension's filter. Each view
projects the tenant's id as a column marked `[TenantId]`, so a read through the view sees the context's tenant's
rows like a read of a table. The seeder opens a session as each tenant and writes nothing about the tenant by
hand; the extension stamps every row. A job that spans tenants disables the extension on its session.

## Views and a table function

The module defines three views in LINQ and one table function in SQL; the upgrade creates them with the tables.
`v_UpcomingAppointments` flattens what is still to come for a diary screen, `v_StylistRevenue` sums what each
stylist brought in, and `mv_LowStock` is materialized:

```csharp
[Entity, View("mv_LowStock", Materialized = true)]
public interface ILowStock {
  [PrimaryKey] Guid Id { get; set; }
  [TenantId] short TenantId { get; set; }
  [Size(100)] string ShopName { get; set; }
  [Size(40)] string Sku { get; set; }
  [Size(120)] string ProductName { get; set; }
  int QuantityOnHand { get; set; }
  int ReorderLevel { get; set; }
}

// In BeautyShopModule:
RegisterView<ILowStock>(
  from s in ViewHelper.EntitySet<IStockLevel>()
  where s.QuantityOnHand <= s.ReorderLevel
  select new { s.Id, TenantId = s.Tenant.Id, ShopName = s.Shop.Name, s.Product.Sku, ProductName = s.Product.Name,
    s.QuantityOnHand, s.ReorderLevel });
```

`v_StylistRevenue` counts and sums over other entity sets. A set a sub-query reads is held in a local outside the
query: `var lines = ViewHelper.EntitySet<ISaleLine>();` and then, in the projection,
`lines.Where(l => l.PerformedBy!.Id == e.Id).Sum(l => (decimal?)(l.UnitPrice * l.Quantity)) ?? 0m`.

The table function `fn_ShopDaySchedule(shop, day)` returns a shop's appointments on one day as `IShopDaySlot`
rows, which have no primary key and are not tracked. Its body is a PostgreSQL select written in the module.

```csharp
var slots = await session.TableFunction<IShopDaySlot>(shopId, new DateOnly(2026, 11, 2)).ToListAsync();
var low = await session.EntitySet<ILowStock>().ToListAsync();   // the tenant's rows, the extension's doing
await session.RefreshMaterializedViewAsync<ILowStock>();
```

A sale's `TotalAmount` is stored and maintained by the code that writes the lines. A sum over another table is
not a generated column on any server, and the sample keeps no trigger.

## Building the database

```bash
dotnet run --project src/Samples/BeautyShop -- --drop
```

`BeautySetup.Main` creates the database `BeautyShop` on the local PostgreSQL if it is missing, connects the
application (the upgrade creates the schema, the tables, the views and the function), runs the seeder, refreshes
`mv_LowStock` and clusters the tables. A second run without `--drop` seeds a second set of tenants. `--drop`
deletes the database first.

The connection string defaults to `BeautyShopApp.DefaultConnectionString`, the test server of the repository.
Pass another connection string as an argument.

## Using it

```csharp
var app = new BeautyShopApp();
app.Init();
await app.ConnectToAsync(new DbSettings(new PostgresProvider(), BeautyShopApp.DefaultConnectionString));

var context = new OperationContext(app);
context.SetValue("TenantId", tenantId);
await using var session = context.OpenSession();
var today = DateOnly.FromDateTime(DateTime.Today);
var appointments = await session.EntitySet<IAppointment>()
  .Where(a => a.Shop.Name == "Downtown" && DateOnly.FromDateTime(a.StartsOn) == today)
  .Include(a => a.Customer)
  .OrderBy(a => a.StartsOn)
  .ToListAsync();
```

The tool configs in the `mic-db` README use this database as their example: `mic-db upgrade` against it writes
the script for its schema, and `mic-db entities` reads it back as a generated model.
