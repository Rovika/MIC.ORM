# Full-text search

Modern database servers provide a full-text search facility: an index over text columns that knows words and
word forms, finds rows by the words they contain and ranks them by relevance. A search with `LIKE` is far more
primitive, a direct substring match, not fit for searching content texts.

Each server has its own syntax for the index, the predicate and the rank. MIC ORM provides one API over them:
the full-text index is declared on the entity, and a query uses two functions, `FullText.Matches` and
`FullText.Rank`, translated to each server's own predicate and rank.

```csharp
[Entity, Index("Name,Description", IndexType = DbIndexType.FullText)]
public interface IProduct {
  [PrimaryKey, Auto] Guid Id { get; set; }
  [Size(100)] string Name { get; set; }
  [Unlimited] string? Description { get; set; }
}

var found = await session.EntitySet<IProduct>()
  .Where(p => FullText.Matches(p, "steel widget"))
  .OrderByDescending(p => FullText.Rank(p, "steel widget"))
  .ToListAsync();
```

`FullText.Matches(row, query, kind, distance)` and `FullText.Rank(row, query, kind, distance)` are translated
to the server's full-text predicate and rank. `TextMatchKind` is `AllWords` by default.
`session.IsFullTextIndexCurrentAsync<IProduct>()` tells whether the server has finished indexing, for servers
that index asynchronously.

A match also finds the rows of a set-based update or delete:

```csharp
await session.EntitySet<IProduct>()
  .Where(p => FullText.Matches(p, "discontinued"))
  .ExecuteDeleteAsync();
```

The upgrade creates the index, drops and recreates it when its columns change, and drops it when it is removed
from the entity.
