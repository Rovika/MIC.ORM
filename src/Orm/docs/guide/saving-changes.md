# Updating data

An application writes data in units: an order with its lines, a customer and the address changed with it, a
batch of rows marked processed. Each unit has to be validated, written in one transaction in the order the
references require, and the values the server generated read back; a change to a thousand rows should not mean
loading a thousand rows.

In MIC ORM the session does this. It tracks what the application creates, changes and deletes, and
`SaveChangesAsync` writes all of it: validated, ordered, in one transaction, in as few round trips as the server
allows. A change to many rows is written as a LINQ query and runs as one statement. This page covers creating,
changing and deleting entities, validation and client faults, the save, transactions, and set-based updates.

## Creating, changing, deleting

```csharp
using System.Data;
using Mic.Linq;
using Mic.Orm;

var session = app.OpenSession();

var customer = session.NewEntity<ICustomer>();
customer.Name = "Ada";

var order = session.NewEntity<IOrder>();
order.Customer = customer;
order.PlacedOn = DateTime.UtcNow;
order.Total = 12.50m;

var old = await session.GetEntityAsync<IOrder>(oldOrderId);
old.Status = OrderStatus.Closed;

var stale = await session.GetEntityAsync<ICustomer>(staleCustomerId);
session.DeleteEntity(stale);

await session.SaveChangesAsync();
```

* `NewEntity<T>()` creates an entity with status `New`. An `[Auto]` key is assigned at creation.
* Assigning a member of a loaded entity sets its status to `Modified`. The save updates the changed members
  only.
* `DeleteEntity(entity)` sets the status to `Deleted`. Deleting a `New` entity removes it from the session.
* `CanDeleteEntity(entity, out Type[] blockingEntities)` and `GetDeleteEntityBlockingTypesAsync(entity)` report
  the entity types whose rows reference the entity through a foreign key without cascade. The check is one
  statement, whatever the number of references.
* `session.GetChangedRecords()` lists what the next save writes; `GetChangeCount()` counts them.
* `session.NewCopyOf(entity)` creates a new entity with the column values of another, without its key, its
  references and its computed members.

## Client faults and validation

A `ClientFault` is a stop in an operation that the client caused and the client can fix: a missing value, an
overlong string, a rule of the application's broken by what is in the database. It is sent back to the client
and shown to the user. A fault has a `Code`, a `Message`, the `MemberName` it is about, and the `Record` for a
fault the ORM or an extension raises about a row.

`ValidateChanges()` checks every pending record and throws `ClientFaultException` with all faults. The save
validates first and throws the same way.

| `ClientFaultCodes` | Raised by |
|---|---|
| `ValueMissing` | the ORM: a non-nullable member has no value. |
| `ValueTooLong` | the ORM: a string exceeds its `[Size]`. |
| `JsonTypeMismatch` | the ORM: a `[Json]` member holds a type its declaration does not allow. |
| `"TenantBoundary"` | the multi-tenancy extension: a row belongs to another tenant. The code is the extension's `BoundaryFaultCode`, not a member of `ClientFaultCodes`. |
| `ValueOutOfRange`, `InvalidValue`, `InvalidAction`, `ObjectNotFound`, `ConcurrentUpdate`, `AuthenticationRequired` | the application, by convention. |

An entity with `[Validator(typeof(OrderRules), "Validate")]` runs that static method after the built-in checks.

```csharp
try {
  await session.SaveChangesAsync();
} catch (ClientFaultException ex) {
  foreach (var fault in ex.Faults)
    Console.WriteLine($"{fault.Record?.Entity.Name}.{fault.MemberName}: {fault.Message}");
}
```

The application raises its own faults on the context during an operation and throws them together:

```csharp
if (await session.EntitySet<IStockItem>().Where(i => i.Id == itemId).Select(i => i.OnHand).FirstAsync() < quantity)
  context.AddClientFault(new ClientFault(ClientFaultCodes.InvalidValue, "Not enough stock.", "Quantity"));
if (customer.Status == CustomerStatus.Suspended)
  context.AddClientFault(new ClientFault(ClientFaultCodes.InvalidAction, "The customer is suspended."));
context.ThrowValidation();      // throws ClientFaultException with both, and clears them; nothing when there are none
```

`ClientFault.EntityRef`, `Tag` and `Parameters` carry what the client needs to show the fault at the right place.

## The save

`SaveChangesAsync(SaveOptions options = SaveOptions.None)`:

1. Applies the list changes: link rows for many-to-many lists, positions for `[PersistOrderIn]`.
2. Validates every pending record.
3. Raises `app.Events.SavingChanges`.
4. Stamps `[Auto]` members: `CreatedOn`, `UpdatedOn`, `CreatedBy`, `UpdatedBy`.
5. Orders the records so that a referenced row is inserted before the row referencing it and deleted after.
6. Writes the records on one connection in one transaction: inserts, updates, deletes. Multi-row statements
   are used where the server allows; `EntitySessionOptions.DisableBatchMode` runs one command per statement.
7. Reads back server-generated values: identities, sequence values, row versions.
8. Commits, marks the records `Loaded` (deleted ones `Fantom`), raises `SavedChanges`.

A failure rolls the transaction back, raises `SaveChangesAborted`, and leaves the records as they were in the
session. `session.GetLastCommands()` shows what ran.

A user deletes the row named "X" in a grid, adds a new "X" and submits; or renames two rows into each other's
names. Inserts run before updates and updates before deletes, so the old row would still hold the unique value
when the other statement takes it. The save finds a unique value that one record frees and another takes, and
first gives the row that frees it a placeholder, in an `UPDATE` of its own at the start of the transaction. The
row is then deleted or gets its real value in the usual order. A trigger or an audit log on the table sees the
placeholder. A placeholder exists for a string column of 12 characters or more, a Guid column and an integer
column holding a value that is not negative. A unique key with no such column, a filtered unique index and the
primary key are not covered; the server refuses the collision, and the save throws `DataAccessException` with
the `SubType` `UniqueViolation`. For such a key, make the change in two saves inside one
[transaction](#transactions): first the delete or the rename that frees the value, then the change that takes
it. Two rows that swap values give one of them a temporary value in the first save.

`SaveOptions.InsertAndForget` writes the records and stops tracking them. Use it for log rows and imports.
Only the keys a child of the same save needs are read back.

## Transactions

A save is one transaction. For a transaction spanning reads and several saves:

```csharp
await session.BeginTransactionAsync(IsolationLevel.ReadCommitted);
try {
  var item = await session.GetEntityAsync<IStockItem>(itemId, LoadFlags.WriteLock);
  if (item.OnHand < quantity)
    throw new InvalidOperationException("Not enough stock.");
  item.OnHand -= quantity;
  await session.SaveChangesAsync();
  await session.CommitTransactionAsync();
} catch {
  await session.RollbackTransactionAsync();
  throw;
}
```

`BeginTransactionAsync(IsolationLevel isolationLevel = IsolationLevel.Unspecified, bool commitOnSave = false)`
opens a transaction on the session's connection; every request until it ends runs inside it. With
`commitOnSave: true`, the next save commits it. `RollbackTransactionAsync` rolls back the database; the tracked
records keep their in-memory values. A context disposed with a transaction open rolls it back.

`LoadFlags.ReadLock` and `LoadFlags.WriteLock` on a read by key, and `QueryOptions.LockForUpdate` on a query,
lock the rows until the transaction ends. See [Locking records](locking.md).

## Set-based updates in LINQ

A change to many rows runs as one statement without loading the rows. A query says which rows and, for an update
or an insert, which values; the session runs it.

```csharp
// DELETE … WHERE
var cancelled = session.EntitySet<IOrder>().Where(o => o.Status == OrderStatus.Cancelled && o.PlacedOn < cutoff);
var deleted = await session.ExecuteDeleteAsync<IOrder>(cancelled);

// UPDATE … SET … WHERE: the projection names the members and their new values.
var old = session.EntitySet<IOrder>().Where(o => o.PlacedOn < cutoff);
var archived = await session.ExecuteUpdateAsync<IOrder>(old.Select(o => new { Status = OrderStatus.Archived }));

// INSERT … SELECT: the projection names the target's members.
var copies = old.Select(o => new { CustomerName = o.Customer.Name, o.PlacedOn, o.Total });
var copied = await session.ExecuteInsertAsync<IArchivedOrder>(copies);
```

| Method | Statement |
|---|---|
| `ExecuteDeleteAsync<T>(query)` | `DELETE` of the rows the query finds. Over `T` itself, the filter names the rows; over another entity, the projection brings `T`'s key. |
| `ExecuteUpdateAsync<T>(query)` | `UPDATE` of `T`'s rows. Over `T` itself, the filter names the rows; over another entity, the projection includes `T`'s key members. |
| `ExecuteInsertAsync<T>(query)` | `INSERT … SELECT` into `T`. Members not projected take their defaults. |

Each has a synchronous form, `ExecuteDelete<T>(query)`, and a form on the query itself,
`query.ExecuteDeleteAsync()`, which the session's calls. The query may filter through references, order and page;
the rows are then found by a subquery. A projection of values with no table, or an ordering without a page, is
refused with `NotSupportedException`.

`ScheduleDelete<T>`, `ScheduleUpdate<T>` and `ScheduleInsert<T>` hold the same statements for the next save, to
run inside its transaction before (`CommandSchedule.TransactionStart`) or after (`CommandSchedule.TransactionEnd`,
the default) the save's own statements. `session.GetScheduledMutations()` lists them. A scheduled statement
binds its captured locals as they are at the call: a local assigned again before the save, or a loop variable,
does not change it. An entity a local refers to is the same entity at the save, so a key the save generates for
it is the key bound.

```csharp
session.ScheduleDelete<IOrderLine>(session.EntitySet<IOrderLine>().Where(l => l.Order.Id == orderId));
session.DeleteEntity(order);
await session.SaveChangesAsync();    // the lines go first, then the order, in one transaction
```

A set-based statement bypasses the session's tracking. Entities the session holds are not updated by it.

## Events

`app.Events` raises `SessionOpened`, `SavingChanges`, `SavedChanges`, `SaveChangesAborted` with the session,
`CommandExecuted` with each command, `Error` with the context and the exception, and `Shutdown` with the
application. `SavingChanges` is where a handler inspects `session.ChangedRecords` and adds or changes records
before they are written. An extension such as `SoftDeleteExtension` works this way: it turns a delete into an
update in `SavingChanges`.

