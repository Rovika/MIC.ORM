# About application extensions

Applications have rules that apply to every entity of a kind rather than to one: every table bound to a tenant
is read for one tenant and written with it; every table that keeps deleted rows hides them and turns a delete
into an update. Such a rule belongs in one place, applied to every query and every save, not repeated per entity
in the code that uses it.

MIC ORM has application extensions for these rules. An extension is one class, added to the application with
`AddExtension`. It sees every entity when the model is built and attaches what the rule needs: a filter that
every read of the entity carries, and handlers on the save events. The queries and the saves in the application
code do not change. Two extensions ship with the ORM: [multi-tenancy](multi-tenancy.md) and
[soft delete](soft-delete.md).

## The mechanism

`AppExtension` has two virtual members. `OnEntityBuilt(entity)` is called once per entity when the model is
built: the extension decides whether the entity is bound, by a marker interface it carries or by a member name,
and attaches a filter with `entity.AddFilter(…)`. `OnModelConstructed(model)` is called once with the finished
model: the extension hooks the events it listens to, on an entity's `Events` or the application's. Extensions
run in the order they were added.

A filter is a condition the LINQ engine adds to every read of the entity: a LINQ query, a read by key, a
QueryMaker query, a smart load, a cache lookup. A condition that reads the operation context, such as the
tenant, is bound when the statement runs, and a cached entity is then cached per tenant. Direct SQL is not
filtered.

## Disabling an extension on a session

A job that works outside what an extension enforces disables the extension on its session: a purge that removes
the rows soft delete marked long ago, a migration that moves rows between tenants, a seeder.

```csharp
var session = context.OpenSession();
session.DisableExtension<SoftDeleteExtension>();

var old = await session.EntitySet<IMemo>().Where(m => m.DeletedOn < cutoff).ToListAsync();
foreach (var memo in old)
  session.DeleteEntity(memo);
await session.SaveChangesAsync();
```

From the call on, for the rest of the session, the extension's filters are left out of every read, a query, a
load by key, a lazy load, a smart load alike, and its write hooks skip the session's saves: the delete above is a
`DELETE`, and a row saved under `MultiTenantExtension` disabled keeps the tenant it was given, or none. The other
extensions stay on. `DisableExtensions()` disables them all. There is no per-query form.

A session with an extension disabled reads nothing from the entity cache, whose blocks were filled under the
filters, and its queries bypass the query cache. An extension written by the application takes part through
`IsDisabledBy(session)`, checked first in each of its write hooks; its filters need nothing, the session leaves
them out by the owner type given to `AddFilter`.

## Writing one

```csharp
using Mic.Linq;
using Mic.Orm;
using Mic.Orm.Model;

public class ArchivedExtension : AppExtension {
  public override void OnEntityBuilt(EntityTypeInfo entity) {
    if (entity.FindMember("IsArchived") is { } member)
      entity.AddFilter(
        QueryMaker.MakeFilter(entity, row => row.Member(member.Name).Equal(QueryMaker.Constant(false))),
        owner: GetType());
  }
}
```

The shipped extensions are written the same way. Each finds its member by an attribute, a configured name or
the marker, so a legacy database configures it rather than derives from it.
