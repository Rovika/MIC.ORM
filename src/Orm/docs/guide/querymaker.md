# QueryMaker

Some applications build queries at run time: a search screen with a column picker, a report builder, a rule
engine. The entity, the members and the conditions arrive as names and values, often from a client, and a LINQ
query needs them as C# expressions when it compiles.

MIC ORM provides QueryMaker for this: a query built from names as a tree of `QueryNode`s, translated,
parameterised and cached by the same LINQ engine as a LINQ query. A client sends a names-only sketch of the
query; the server rebuilds the tree from it and runs it. A misspelled name is found when the tree is built, not
when the statement runs.

```csharp
using Mic.Linq;
using Mic.Orm;

var query = session.From("Order")
  .Where(row => QueryMaker.And(
    row.Member("Customer").Member("Name").Equal(QueryMaker.Parameter("name", typeof(string))),
    row.Member("Total").GreaterOrEqual(QueryMaker.Constant(100m))))
  .OrderBy(row => row.Member("PlacedOn"), descending: true)
  .Take(QueryMaker.Constant(10));

var records = await session.ExecuteQueryAsync(query, "Ada");    // entity records; record.EntityInstance is the entity
var rows = await session.ExecuteRowsAsync(projected);            // untyped rows of a projection
var count = await session.ExecuteScalarAsync<int>(counting);     // one value
```

`row.Member("Name")` names a member; a misspelled name throws when the tree is built. `QueryMaker.Parameter`
declares a parameter whose value is passed to the execute call. `QueryMaker.Constant` embeds a value. The
operators are methods (`Equal`, `NotEqual`, `Less`, `Greater`, `In`, `And`, `Or`, `Not`, `TextMatches`) and C#
operators on `QueryNode`. `query.Include("Customer")` is the QueryMaker form of the LINQ call on [Reading
data](linq-queries.md).
