# Multi-tenancy extension

A multi-tenant application serves many customers, tenants, from one database: each tenant's rows carry its id,
and an operation run for one tenant reads and writes that tenant's rows and no other's. The rule holds for every
query and every insert in the application, and it is the one most often broken when written by hand.

MIC ORM's `MultiTenantExtension` applies it. An entity bound to a tenant is read for the context's tenant only,
and a row it saves is stamped with it. The extension serves a new application and an existing database alike;
what differs is how it finds the member that holds the tenant.

## A new application

The ORM ships two interfaces: `ITenant`, the tenant table, and `ITenantBound`, the marker of a bound entity, a
reference to the tenant with an index on it. The application registers `ITenant` in a module, inherits
`ITenantBound` on its bound entities, and adds the extension through its parameterless constructor, with nothing
to configure:

```csharp
using Mic.Orm.Extensions.MultiTenancy;

[Entity]
public interface IOrder : ITenantBound {
  [PrimaryKey, Auto] Guid Id { get; set; }
  …
}

// In the module:
RegisterEntities(typeof(ITenant), typeof(IOrder), …);

// In the application's constructor:
AddExtension(new MultiTenantExtension());

// Per operation, the host sets the tenant from the request:
var context = new OperationContext(app, user);
context.SetValue("TenantId", tenantId);
```

`ITenant.Id` is a `short`, assigned rather than generated: it repeats in every bound table, so its width is the
one choice that costs storage everywhere. The reference keeps the integrity: a bound row references a tenant that
exists. The extension works on the reference's key column, `Tenant_Id`: the condition compares it, the stamp
writes the tenant's id into it without loading the tenant, and a read of `order.Tenant` resolves the tenant from
the key.

The tenant is infrastructure, not business domain. A company or an organization of the domain, with its name,
its contacts and its own entities, is a business record a top-level entity references; it is not the tenant.

## An existing database

A legacy database holds its tenant in a column of its own naming, a company code or a customer id, and the
entities cannot inherit a marker. The extension's other constructor takes the member's name:

```csharp
AddExtension(new MultiTenantExtension("CompanyCode"));
```

Every entity with a member of that name is bound. The member may be a scalar, the code itself, or a reference
to the company's entity; for a reference the extension works on its key column, as it does for `ITenantBound`.
Two attributes handle the exceptions: `[TenantId]` on a member binds it whatever its name, and `[TenantId(false)]`
on the member of the configured name excludes the entity. An attribute wins over the name and over the marker, and
the name replaces the marker: with it set, `ITenantBound` is not consulted.

The tenant's type is the column's, a `string` for the code above, and every bound entity has the same one; two
types in one model fail the build with the two entities named. The context's value is converted to it, so an
`int` set for a `short` column names the same tenant.

## What the extension does

The tenant of an operation is a value of its `OperationContext`, the object the host builds per request and opens
the session from: `context.SetValue("TenantId", tenantId)`, then `context.OpenSession()`. The key is the
extension's `TenantIdKey`, `"TenantId"` by default. The filter reads the tenant off the session's context when the
statement runs, and the stamp writes it from there at save. A session whose context names no tenant sees no rows
of a bound entity, and a row it saves with no tenant of its own is refused. A row saved under another tenant is
refused with a `ClientFault` of code `TenantBoundary`, or overwritten with the context's tenant when
`BoundaryMode` is `TenantBoundaryMode.Enforce`.

A cached entity is cached per tenant. A job that spans tenants disables the extension on its session,
`session.DisableExtension<MultiTenantExtension>()`: it reads every tenant's rows and writes rows with the tenant
they carry. See [Disabling an extension on a session](app-extensions.md#disabling-an-extension-on-a-session).

A view is one database object for every tenant: its definition is translated without the filters, and the tenant
is enforced where the ORM reads from it, only when the view entity is itself bound. A view entity with no tenant
member shows every tenant's rows, and the build report carries a warning for it.

## Tenants across databases

There is another approach to multi-tenancy: a separate database for each tenant. MIC ORM supports it. An
application connects to any number of databases, each under its own `DatabaseAlias`; the target alias is part
of the user's or the client's information, and the request sets it in `OperationContext.DatabaseAlias`. The
extension is not needed: the database is the tenant. See [Several databases](startup.md#several-databases).

There is also a mixed approach: several databases, each hosting several tenants. It serves an application whose
tenants are too many for one database. Another factor is size: tenants differ widely, and the very big ones,
usually the biggest payers, need a database just for them. The distribution of tenants is uneven, some databases
with one big tenant, others with many smaller ones.

In the mixed approach the tenant key assigned to a user or a client is composite, the database alias and the
tenant id: `"Db23A.123"`. The request initialization code reads the key from a header or a JWT token, splits it
and sets the database alias and the tenant in the `OperationContext`:

```csharp
var parts = tenantKey.Split('.');
var context = new OperationContext(app, user) { DatabaseAlias = parts[0] };
context.SetValue("TenantId", short.Parse(parts[1]));
```

The mixed approach gives capacity planning the most room, for tenants large and small. It covers the other two
as its ends: one database with every tenant, and a database per tenant, each holding one. A new tenant goes to
a database with room; a tenant that grows moves to a database of its own by copying its rows, the schema being
one everywhere, and its tenant key changes while the application does not. The extension works the same in
every database: a database with one tenant is filtered and stamped like the others. The caches are per database;
a cached entity is cached per database and per tenant.

### A database added at run time

A multi-tenant application should be able to add tenants and databases while it runs, with no change of
configuration and no restart. The application serves its tenants; an administrator prepares the database of a
new tenant and enters it in the catalog behind the user-to-tenant lookup. The first user of the tenant hits the
server, the request names a database alias the application has not connected, and the application looks the
alias up in the catalog, finds the new database and connects it.

MIC ORM supports this with `DatabaseEvents.DatabaseMissing`. The event is raised when a request names an alias
with no database registered under it, before the request is refused; the handler makes the fresh lookup and
connects the database, and the request proceeds on it:

```csharp
shopApp.DatabaseEvents.DatabaseMissing += (app, alias) => {
  var connectionString = tenantCatalog.FindConnectionString(alias);
  if (connectionString != null)
    app.ConnectTo(new DbSettings(new PostgresProvider(), connectionString, databaseAlias: alias));
};
```

The handler runs on the request's thread, once, the first time a request names the database: `ConnectTo`, the
synchronous form, checks connectivity and upgrades the schema before the database serves its first request. A
handler that connects nothing leaves the refusal as it was. A database known ahead of its first request is
connected the same way without the event, `ConnectToAsync` under its alias at any time after startup.
