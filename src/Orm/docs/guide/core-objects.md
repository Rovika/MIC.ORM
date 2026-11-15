# Core runtime objects

An application's code runs in operations: a web request, a background job, a test. The code of an operation
needs to know which user it runs for, to reach the application's services and databases, to keep state while the
operation lasts, and to read and change data. An operation passes through several layers of code, and every
layer needs the same things.

MIC ORM has two objects for this. An `OperationContext` is the context of one operation: it holds the user, the
application, the operation's state and, in a web application, the current request. An `EntitySession` is opened
from a context and does the data access: it runs the queries, tracks the entities it loads and the changes made
to them, and writes the changes to the database. This page outlines how the two are used. Queries are on
[Reading data](linq-queries.md); writes on [Updating data](saving-changes.md).

## OperationContext

An `OperationContext` is the context holder for the application's code. Create one when an operation starts,
pass it to the code that runs the operation, and dispose it when the operation ends.

```csharp
using var context = new OperationContext(app, new UserInfo(userId, "ada"));
var session = context.OpenSession();
```

The context holds:

* **The user.** `User` is a `UserInfo`: an authenticated user, `UserInfo.Anonymous`, or `UserInfo.System` for
  work the application does on its own behalf, such as a background job. Set it when the operation starts, from
  the auth token of the request or from the login. A context created without a user is anonymous.
* **The application.** `App` is the `EntityApp`, and through it the code reaches the services and the connected
  databases. `GetService<T>()` returns a service by its interface. `OpenSession()` opens an `EntitySession` for
  queries and updates.
* **The operation's state.** The context lives for the span of the operation. `Values` is a collection of named
  values: code sets one with `SetValue(key, value)`, and code that runs later in the operation reads it with
  `GetValue<T>(key)`. The tenant of a multi-tenant application is such a value.
* **The web request.** In a request/response application the context holds the current request. `HttpContext`
  is the host's request object, ASP.NET Core's `HttpContext`; it is typed `object`, since MIC ORM references no
  web framework. `Services` is the request's service provider. `WebRequestId` and `CorrelationId` mark the
  operation's entries in the log.

The context also carries the operation's log and the client faults the code raises: see [Logging](logging.md)
and [Updating data](saving-changes.md). Disposing the context ends the sessions it opened, closes their
connections and posts the log.

In an ASP.NET Core application the context is created per request by the container: see [Setup in ASP.NET
Core](aspnet-core.md).

## EntitySession

An `EntitySession` is an extended connection object. All data access runs through it: the queries and the
updates. Open a session from a context; the code sees it as `IEntitySession`.

```csharp
var session = context.OpenSession();

var customer = await session.GetEntityAsync<ICustomer>(customerId);
var openOrders = await session.EntitySet<IOrder>()
  .Where(o => o.Customer.Id == customerId && o.Status == OrderStatus.Open)
  .ToListAsync();

var order = session.NewEntity<IOrder>();
order.Customer = customer;
customer.LastOrderOn = DateTime.UtcNow;
await session.SaveChangesAsync();
```

* **Tracking.** The session tracks every entity it loads and every change the code makes: an entity created, a
  member assigned, an entity deleted. Nothing is written until `SaveChangesAsync()`, which writes all the
  tracked changes in one transaction.
* **One database.** A session works with one database: the one named by the context's `DatabaseAlias`, or the
  default one. See [Several databases](startup.md#several-databases).
* **One tenant.** In a multi-tenant application a session reads and writes the rows of one tenant, the one set
  in the context's values.
* **Lifetime.** A context can open several sessions. Do not dispose a session: it registers with its context
  and ends with it.
* **Threads.** A context is free-threaded. A session is single-threaded: its structures are not thread-safe, and
  it runs one operation at a time. See [One operation at a time](#one-operation-at-a-time).

A test or a console program can skip the context: `app.OpenSession()` opens a session on a new context with an
anonymous user, and `app.OpenSystemSession()` one with the system user.

The session also runs transactions, set-based updates, queries built at run time and direct SQL: see [Updating
data](saving-changes.md), [QueryMaker](querymaker.md) and [Direct SQL](direct-sql.md).

### Entity tracking

A session holds one instance per entity and primary key. The customer with `Id` 123 is tracked in a session as
a single instance. Every later query in the session that brings a customer row with this `Id` returns that same
instance in its result; the row's values are not applied to it. Another session has its own instance of the
customer.

The session tracks a loaded entity through a weak reference, unless the entity is modified. The entity stays
tracked as long as the application's code holds a reference to it. When the garbage collector collects the
instance, the session drops it. A session that reads a large number of rows, batch after batch, is therefore in
no danger of running out of memory: the entities the code no longer holds are collected. A new, modified or
deleted entity is held by the session until the save.

A reference member read on a loaded entity returns the tracked target when the session holds it, or loads it.
`GetEntity<T>(key)` returns the tracked entity when there is one and queries otherwise.

### One operation at a time

A desktop form or a Blazor Server component keeps one session for as long as it lives. Its handlers await, and
`await` frees the user-interface thread: a second click runs while the first handler's save is still at the
server. Both operations then use the session's one connection. The same happens when a session is handed to a
second thread, and when two queries of one session are started together and awaited with `Task.WhenAll`.

A session refuses the second operation with `InvalidOperationException`, "The session is already running an
operation", and the first one completes. While a save is at the server the session refuses a change to its
entities the same way: a property assigned, an entity created or deleted, a list changed, a statement
scheduled, a second save. The save would mark such a change as written. An entity's `Loaded` handler runs
while the rows of a query are read, so an operation it starts on the same session is refused as well.

A session opened for one request, with every call awaited before the next, never meets this and needs nothing.
For the other kind, `EntitySessionOptions.AddThreadSafety` makes the session's operations take turns:

```csharp
var session = context.OpenSession(EntitySessionOptions.AddThreadSafety);
```

* An asynchronous member called while an operation runs waits for it to end, then runs.
* A synchronous member waits the same way on a thread with no synchronization context. On a user-interface
  thread it cannot wait, since the running operation continues on that thread, and it throws
  `InvalidOperationException`. A bound control that reads a lazy reference during a save is this case: load what
  the control shows before the save starts.
* A handler of the application's or an entity's events, called inside an operation, uses the session without
  waiting.

The turns are of single calls. A handler that reads an entity, changes it and saves makes three calls, and
another handler's call can run between them: its `SaveChangesAsync` writes the first handler's unfinished
changes. Where that matters, the application disables the second action while the first runs. A change to an
entity takes no turn: made while a save is at the server, it is refused with the option as without it. The
option does not make an entity safe to change from two threads at once.
