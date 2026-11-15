# Direct SQL

Database servers have more than any translator writes: server-specific functions and hints, tables outside the
model, a report query tuned by hand. An application needs to run such a statement as text, and still on the
session's connection, inside its transaction, in its log.

MIC ORM runs SQL text through the session. The statement gets the session's connection and open transaction,
its log and timing, and its parameters are passed as values. The result comes back as a table, as typed rows,
as one value or as a row count.

```csharp
using Mic.Orm;

// Rows as they came: an InfoTable, a value by column name.
InfoTable table = await session.RunSqlQueryAsync("""
  SELECT c."Name", COUNT(*) AS "Orders"
  FROM shop."Customer" c JOIN shop."Order" o ON o."Customer_Id" = c."Id"
  WHERE o."PlacedOn" >= @since
  GROUP BY c."Name"
  """, ("since", since));
foreach (InfoRow row in table.Rows)
  Console.WriteLine($"{row.GetString("Name")}: {row["Orders"]} orders");

// Rows as entities: the columns matched by name, the key among them; the session tracks them.
IReadOnlyList<IOrder> open = await session.RunSqlQueryAsync<IOrder>(
  """SELECT * FROM shop."Order" WHERE "Status" = @status""", ("status", 1));

// Rows as objects built by column name, as one value each, one value, a row count.
public record CustomerOrders(string Name, int Orders);
IReadOnlyList<CustomerOrders> summary = await session.RunSqlQueryAsync<CustomerOrders>(sameSql, ("since", since));
IReadOnlyList<string> names = await session.RunSqlQueryAsync<string>(
  """SELECT "Name" FROM shop."Customer" WHERE "Status" = @status""", ("status", 1));
decimal? total = await session.RunSqlScalarAsync<decimal>("""SELECT SUM("Total") FROM shop."Order" """);
int updated = await session.RunSqlNonQueryAsync(
  """UPDATE shop."Order" SET "Status" = 3 WHERE "PlacedOn" < @cutoff""", ("cutoff", cutoff));
```

Parameters are written `@name` in the text and passed as `(name, value)` tuples; the prefix is rewritten for the
server. `RunSqlQueryAsync` returns an `InfoTable`: `Columns` and `Rows`, a row read by column name through the
indexer or `GetString`, `GetInt`, `GetLong`, `GetDateTime` and `GetBool`. `RunSqlQueryAsync<T>` reads each row
as `T`: an entity interface gives the session's tracked entities, with the key column among those selected; a
class or record is built per row by column name; a scalar type takes the first column. The SQL is the server's
own; it is not translated, and an extension's filters do not apply to it. A statement that writes rows of a
cached entity names the entity types it touches, `RunSqlNonQueryAsync(sql, [typeof(IOrder)], …)`, so that the
[data cache](data-cache.md) drops them.
