# Database objects

A database has more in it than tables: indexes that make a query fast, conditions every row has to meet, views
that name a query, sequences that number rows, functions that return rows, columns the server computes. An
application uses them, and they have to be created and kept in step with the tables on every database the
application runs on.

In MIC ORM, the application declares these objects in the model, beside the entities; the upgrade creates and
maintains them with the tables, and a session uses each in a typed way: a view is queried like a table, a
sequence feeds a member, a table-valued function composes with LINQ. This page shows each declaration and its
use. Full-text indexes are on [Full-text search](full-text.md); temp tables on [Temp tables](temp-tables.md).

## Indexes

`[Index]` and `[Unique]` declare an index the same two ways a primary key is declared: on a member, over that
member; on the interface, over the members named, comma or space separated, a `-` before a member making it
descending. `[Index(IncludeMembers = "…")]` adds covering columns, `[Index(Filter = "…")]` a condition,
`[Index(IndexType = DbIndexType.FullText)]` a full-text index. An entity may carry any number.

```csharp
[Entity, Index("Customer,PlacedOn"), Unique("Customer,Number")]
public interface IOrder {
  [PrimaryKey, Auto] Guid Id { get; set; }
  ICustomer Customer { get; set; }
  [Size(20)] string Number { get; set; }
  [Utc] DateTime PlacedOn { get; set; }
  [Index] OrderStatus Status { get; set; }
}
```

A reference is a foreign-key constraint unless `[EntityRef(HasDbForeignKey = false)]` says otherwise, or the
database's `DbOptions.UseRefIntegrity` is off; `DbOptions.AutoIndexForeignKeys` indexes every foreign key. See
[Application startup](startup.md#connecting-to-the-database).

## Check constraints

A price is never negative, and an offer never ends before it starts, whoever writes the row: the application, a
script, another program. The server enforces such a rule when the table carries it as a check constraint.

`[Check(name, condition)]` on the interface declares one. The condition is SQL as the server reads it; a member
written as `{Name}` stands for its column, quoted as the server quotes. A braced word inside quoted text is
text and is left as written: in `{Zip} ~ '^[0-9]{5}$'` only `{Zip}` is a member.

```csharp
[Entity]
[Check("Price", "{Price} >= 0")]
[Check("Period", "{EndsOn} >= {StartsOn}")]
public interface IOffer {
  [PrimaryKey, Auto] Guid Id { get; set; }
  decimal Price { get; set; }
  DateOnly StartsOn { get; set; }
  DateOnly EndsOn { get; set; }
}
```

A condition one server spells differently is declared again under the same name with the server kind first,
`[Check(DbServerKind.MsSql, "Code", "LEN({Code}) = 3")]`, and takes the place of the general declaration there.

A server rewrites the condition it is given, so the text it reports cannot be compared with the declaration. The
constraint's name carries the comparison: `CK_`, the table, the declared name and eight hex digits of a hash of
the condition, as in `CK_Offer_Price_3fa9c1d2`. The upgrade compares names. A changed condition is a new name:
the old constraint is dropped and the new one added, and the add fails where existing rows break the new
condition. A constraint under any other name, one a DBA added, is not the upgrade's and stays.

SQLite takes a constraint only in the statement that creates the table. On a table that exists the upgrade
reports the change and does not make it.

## Views

A view is an entity whose rows come from a query. The entity interface gives the view its columns; the module
gives it its definition. A view entity is read-only.

```csharp
using Mic.Orm;

[Entity, View("v_CustomerTotals")]
public interface ICustomerTotal {
  [PrimaryKey] Guid Id { get; set; }
  [Size(100)] string Name { get; set; }
  int OrderCount { get; set; }
  decimal Total { get; set; }
}
```

The view is registered in the module in one of five forms:

```csharp
// 1. A typed LINQ query over ViewHelper.EntitySet<T>(); the projection names a value for every member.
RegisterView<ICustomerTotal>(
  from c in ViewHelper.EntitySet<ICustomer>()
  select new { c.Id, c.Name, OrderCount = c.Orders.Count(), Total = c.Orders.Sum(o => o.Total) });

// 2. A QueryMaker tree built against the model: RegisterView<ICustomerTotal>(Func<ILinqDataModel, QueryNode>).

// 3. SQL, one text per server.
RegisterView<ICustomerTotal>(new Dictionary<DbServerKind, string> {
  [DbServerKind.Postgres] = "SELECT c.\"Id\", c.\"Name\", … FROM shop.\"Customer\" c …",
  [DbServerKind.MsSql] = "SELECT c.[Id], c.[Name], … FROM shop.[Customer] c …",
});

// 4. SQL for one server.
RegisterView<ICustomerTotal>(DbServerKind.Postgres, "SELECT …");

// 5. No definition: the view exists in the database already, maintained by scripts outside the model.
RegisterExistingView<ICustomerTotal>();
```

A sub-query over another entity set is written over a local held outside the query, `var orders =
ViewHelper.EntitySet<IOrder>();`, and used inside the projection: `orders.Count(o => o.Customer.Id == c.Id)`.

A view that reads another view names it: `RegisterView<IShopTotals>(query, dependsOn: [typeof(ICustomerTotal)])`.
The named view is created first, and recreated with its dependents when its definition changes. A view is also
recreated when a column of a table it reads changes type or is dropped, since PostgreSQL refuses the change
under a view; a query knows its tables, an SQL view names them in `dependsOn` too.

The upgrade creates the view and stores a hash of its definition. A changed definition drops and recreates the
view. An existing view, form 5, is also what `[View]` alone declares, with no registration in the module; MIC
reads through it and never creates, changes or drops it. The upgrade checks that it has a column for every
member.

`ViewOptions.Materialized`, or `[View(Materialized = true)]`, makes the view materialized on PostgreSQL and
Oracle. Other servers create an ordinary view. `session.RefreshMaterializedViewAsync<ICustomerTotal>()` refreshes
it, and does nothing on a server where the view is ordinary.

A view entity is queried like a table: `session.EntitySet<ICustomerTotal>().Where(…)`.

A view over a `GROUP BY` or a join often has no column that identifies a row. Such a view declares no
`[PrimaryKey]`:

```csharp
[Entity, View("v_SalesByRegion")]
public interface IRegionSales {
  Guid RegionId { get; set; }
  DateOnly Month { get; set; }
  decimal Total { get; set; }
}
```

The session tracks the rows of a view with a key as it tracks a table's: a row read twice is one object, and
`GetEntity` finds it. The rows of a view without a key are not tracked, and each read makes new objects. A
one-row read such as `FirstAsync` needs an `OrderBy` there, having no key to order by. A query filters, orders
and groups such rows by their members; one that compares, joins or groups by the row itself is refused. A view
without a key cannot be cached.

## Sequences

A sequence is declared in the module and used by a member with `[Sequence]`.

```csharp
public class ShopModule : EntityModule {
  public ShopModule() : base("Shop", new Version(1, 0)) {
    RegisterEntities(typeof(IOrder));
    RegisterSequence("OrderNumbers", start: 1000, increment: 1);
  }
}

[Entity]
public interface IOrder {
  [PrimaryKey, Auto] Guid Id { get; set; }
  [Sequence("OrderNumbers")] long Number { get; set; }
}
```

`RegisterSequence(string name, long start = 1, long increment = 1, Type? dataType = null)` declares the sequence
in the module's schema; a dotted name, `"billing.Numbers"`, puts it in another schema. The data type defaults to
`long`. The save takes the next value for every new row. `session.GetNextSequenceValueAsync("OrderNumbers")`
and `GetNextSequenceValuesAsync("OrderNumbers", count)` take values directly. MySQL and SQLite have no
sequences. On those servers the mapping drops the sequence with a warning, and a member marked `[Sequence]` is an
ordinary column the application fills; `GetNextSequenceValueAsync` throws `NotSupportedException`.

## Table-valued functions

A table-valued function is a server function that returns rows. The entity interface describes the rows; the module
declares the function with its parameters and, for a function MIC owns, its body per server.

```csharp
public interface IOrderAbove {
  [PrimaryKey] Guid Id { get; set; }
  decimal Total { get; set; }
}

// A function MIC creates and maintains:
RegisterTableFunction<IOrderAbove>("OrdersAbove", new Dictionary<DbServerKind, string> {
  [DbServerKind.Postgres] = "SELECT \"Id\", \"Total\" FROM shop.\"Order\" WHERE \"Total\" >= min_total",
  [DbServerKind.MsSql] = "SELECT [Id], [Total] FROM shop.[Order] WHERE [Total] >= @min_total",
}, ("min_total", typeof(decimal)));

// A function that exists already:
RegisterExistingTableFunction<IOrderAbove>("OrdersAbove", ("min_total", typeof(decimal)));

// In a session:
var large = await session.TableFunction<IOrderAbove>(500m).Where(o => o.Total < 10000m).ToListAsync();
```

`session.TableFunction<TRow>(params object?[] arguments)` returns an `IQueryable<TRow>` that composes with
`Where`, `OrderBy`, joins and projections. The rows' primary key is optional, as a view's is. PostgreSQL, SQL
Server and Oracle have table-valued functions. MySQL and SQLite do not; a call there throws with the function's
name.

A function can be called with a value of the row beside it, once for each row:

```csharp
var pairs = await (
  from c in session.EntitySet<ICustomer>()
  from o in TableFunctions.Call<IOrderAbove, decimal>(c.CreditLimit)
  select new { c.Name, o.Total }).ToListAsync();
```

PostgreSQL and SQL Server run it as a lateral join. On Oracle the query throws: the server fails on such a call.

## Computed columns

Not every member is a value the user entered: a line total is the quantity times the price, a weight sits inside
a JSON document, a description is composed from other members. Three attributes declare such a member, and they
differ in who computes the value:

* `[DbComputed]`: the server, from a SQL expression over the row's columns, written per server with
  `[SqlExpression]`.
* `[FromJson]`: the server, from a path inside a `[Json]` member of the row.
* `[Computed]`: the ORM, by a static method in C# over the entity, on every read.

See [C# attributes for modeling](attributes.md) for their properties.

```csharp
[Entity]
public interface IOrderLine {
  [PrimaryKey, Auto] Guid Id { get; set; }
  int Quantity { get; set; }
  [Column(Precision = 12, Scale = 2)] decimal UnitPrice { get; set; }

  // Computed by the server from an expression; {Name} stands for the member's column, quoted for the server.
  [DbComputed(Stored = true)]
  [SqlExpression(DbServerKind.Postgres, "{Quantity} * {UnitPrice}")]
  [SqlExpression(DbServerKind.MsSql, "{Quantity} * {UnitPrice}")]
  decimal LineTotal { get; }

  // Computed by the server from a value inside a JSON member.
  [Json] LineSpec? Spec { get; set; }
  [FromJson("Spec", "$.Weight")] decimal? Weight { get; }

  // Computed by a static method of the module on every read; no column unless Stored = true.
  [Computed("GetDescription")] string Description { get; }
}
```

A `[DbComputed]` or `[FromJson]` member declares a generated column: the server computes the value from the
expression or from the path of the document. The column is a column of the table like any other: an index covers
it, and a query filters and orders on it.

`Stored` sets how the server keeps the value. By default the column is virtual: the server computes the value
on each read, and it takes no space in the row. With `Stored = true` the server computes the value once, at
insert and update, and writes it into the row; a read takes it as any other column, at the price of the row's
space. A stored column suits a value read often, or one computed from a large document; a virtual one suits a
value read rarely, or rows that change often. Not every server has both kinds: PostgreSQL before 18 stores every
generated column, Oracle and SQLite compute every one on read; there the server's kind wins over the declaration.

A server rewrites the expression it is given, so the upgrade does not compare the text. It writes a hash of the
expression beside the column, as `mic:h=` and hex digits, and compares the hash on the next upgrade. A changed
expression, or a changed path of a `[FromJson]`, drops the column and adds it again, with the indexes over it. A
generated column created by hand has no hash beside it and is recreated the same way, once.

Where the hash is kept decides what a description of the column may do:

| Server | The hash is in | A description |
|---|---|---|
| SQL Server | the extended property `MIC_DefinitionHash` | goes in `MS_Description`, as for any column |
| PostgreSQL, Oracle | the column's comment | goes in the same comment, beside the hash: `Total with tax mic:h=4f1c…` |
| MySQL | the column's `COMMENT` | goes in the same comment, beside the hash |
| SQLite | a comment in the table's text | has no place of its own |

A comment written without the hash on PostgreSQL, Oracle or MySQL makes the next upgrade recreate the column,
which on a stored column rewrites the table. The hash of a filtered index's filter is kept the same way, in the
index's comment or, on SQL Server, in the index's `MIC_DefinitionHash` property.

A `[Computed]` member is the application's own code: the method takes the entity and returns the member's type,
found by name on the module, or on the class `MethodClass` names. By default the member has no column: the value
is computed on each read, in memory, and a query cannot filter on it. With `Stored = true` the ORM writes the
value into a column at every insert and update: a query filters and orders on the column, an index covers it, and
a record outside a session, on a client, has the value. A read in a session still runs the method. The server has
no formula for the column and never recomputes it: a row changed outside the ORM, by direct SQL, keeps the old
value until the ORM saves it again.

`Stored = true` means the same on the three attributes, a column holding the value from the last insert or
update; they differ in who writes it, the server from its expression or the ORM from its method.

## The objects in the upgrade

| Object | Created | Changed | Dropped |
|---|---|---|---|
| Index | yes | drop and create when its columns or options differ | when removed |
| Check constraint | yes | drop and create under a new name when its condition differs | when removed |
| Check constraint under another name | no | no | no |
| View with a registered definition | yes | drop and create when the definition hash differs | when removed from the module |
| Existing view | no | no | no |
| Sequence | yes | no | when removed from the module |
| Table-valued function with a body | yes | drop and create when the body differs | when removed from the module |
| Existing table-valued function | no | no | no |
| Computed column | yes | drop and create when the hash of its expression differs | when removed |
| Full-text index | yes | drop and create | when removed |
