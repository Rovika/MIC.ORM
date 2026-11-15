# Temp tables

Database servers have temporary tables: a table a connection creates for its own use, fills, queries and drops,
invisible to other connections. An application needs one when a result is assembled in steps: the customers a
campaign targets are the ones that several queries find, each with its own conditions, and the final read joins
the collected ids to the customer table. A query over a large list of values is the other case, tens of
thousands of ids picked on a screen or read from a file, more than a parameter or an `IN` list carries.

In MIC ORM, the application declares a temp table as an entity. The session creates it on its connection, fills
it from queries or from objects, and the table joins in LINQ like any entity set. It is dropped with the
connection.

```csharp
public interface ITempVipCustomerIds {
  Guid Id { get; set; }
}

// In the module:
RegisterTempTable<ITempVipCustomerIds>();

// In a session. The VIP customers: those with a large order this year, and those with a large lifetime spend.
// Two inserts collect their ids, one query reads the customers.
var vips = session.CreateTempTable<ITempVipCustomerIds>();
await session.ExecuteInsertAsync<ITempVipCustomerIds>(session.EntitySet<IOrder>()
  .Where(o => o.PlacedOn >= yearStart && o.Total >= 10_000)
  .Select(o => new { Id = o.Customer.Id }));
await session.ExecuteInsertAsync<ITempVipCustomerIds>(session.EntitySet<ICustomer>()
  .Where(c => c.LifetimeSpend >= 100_000)
  .Select(c => new { c.Id }));
var customers = await session.EntitySet<ICustomer>()
  .Where(c => vips.Any(v => v.Id == c.Id))
  .OrderBy(c => c.Name)
  .ToListAsync();
```

`CreateTempTable<T>()` registers the table; the session creates it on its connection before the first statement
that follows, here the first insert. `ExecuteInsertAsync<T>(query)` is the set-based insert of
[Updating data](saving-changes.md#set-based-updates-in-linq): the projection names the table's members. A
customer both queries find is in the table twice, which `Any` does not mind.

`CreateTempTable<T>(rows)` fills the table from objects instead, in one round trip: ids picked on a screen, as
anonymous objects or records whose properties match the members by name. `CreateTempTableAsync<T>(rows)` creates
and fills it at once, for direct SQL against it next. On SQL Server and Oracle the rows go over the server's bulk
channel, `SqlBulkCopy` and an insert with array-bound parameters; on the other servers a multi-row insert
measured as fast.

The entity has plain column members and nothing else; a primary key is optional. Its rows are read-only in the
session. The table is never part of an upgrade.

A query parameter holding a list of rows is staged in a temp table automatically when its JSON form exceeds
`DbSettings.RowsTempTableThreshold`, 500,000 characters, about 10,000 Guid keys. Under it the rows travel as one
bound value. That measured faster at every size on PostgreSQL, MySQL and SQLite; on SQL Server and Oracle the
table catches up at about that size.
