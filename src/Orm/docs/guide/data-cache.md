# Data cache

Every application has reference tables: countries, currencies, product categories, status codes. They are
small, they change rarely, and nearly every operation reads them. An application wants such a table in memory,
read from the server once, and the queries over it answered without a round trip, while the code keeps writing
the same queries.

MIC ORM provides the data cache for this. The application marks the entity as cached, and the LINQ engine
answers the queries over it from memory, filters, ordering and paging included; nothing in the queries changes.

## Marking an entity as cached

In the application's constructor:

```csharp
SetCached(CacheType.Server, [typeof(ICountry), typeof(IProductCategory)], TimeSpan.FromMinutes(10));
```

`SetCached(CacheType cacheType, Type[] entityTypes, TimeSpan? expiration = null)` declares the entities whose
rows are held. The cache type names the tier of the application that holds the rows, not the database server.
`CacheType.Server` is the application server, the process that runs the ORM: this is the cache this page
describes. `CacheType.Client` marks the entity for a cache on a UI client, such as a browser application; the
ORM only carries the mark in the model, for the client runtime to read. The two are independent flags: an entity
cached in both tiers is declared `CacheType.Server | CacheType.Client`, and one delivered to the client once and
not kept on the server is `CacheType.Client` alone. The expiration says how long the rows
are kept before they are read again, for a table another application may write;
`EntityApp.DefaultCacheExpiration`, 60 seconds, applies when none is given.

## Queries over cached entities

The rows are loaded on the first read and kept. From then on, a read by key, and a LINQ query over cached
entities, with its filter, ordering, grouping and paging, run over the rows in memory. A query that joins a
cached entity with others runs on the server, with the part over the cached entity evaluated in memory and
passed to the statement as a value; the server reads nothing from the cached table.

The cache belongs to the database: one copy of the rows, read once, serves every session of the application. An
extension that filters an entity by the operation's context segregates it. Under the
[multi-tenancy extension](multi-tenancy.md), each tenant has its own block, filled from its rows alone, and an
update under one tenant invalidates that tenant's block only.

Any update to a cached entity invalidates its cached block; the block is reloaded on the next query. A query with
`QueryOptions.SkipEntityCache` and a read with `LoadFlags.SkipEntityCache` go to the server, and so does every
sub-query of that query over a cached entity. A query that locks its rows reads the server the same way; a query
that ignores an extension's filters never runs over the cache.
