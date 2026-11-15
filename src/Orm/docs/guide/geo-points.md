# Geographic points

Many applications keep places: shops, customers' addresses, delivery stops, vehicles on the road. The questions
over them are spatial: what is within ten kilometres of here, which shop is the nearest, how far is the courier
from the door. Latitude and longitude in two `double` columns do not answer them well: a distance on the Earth's
surface is a formula the query has to spell, and no index helps a filter written that way.

Database servers have a geographic type for this: a point on the Earth, a spatial index over it, and functions
that measure on the Earth's figure. Each server has its own type, its own index and its own function names:
PostGIS `geography` on PostgreSQL, `geography` on SQL Server, `point` with an SRID on MySQL, `SDO_GEOMETRY` on
Oracle. MIC ORM provides one type, one index and one function over them.

## A point member

In MIC ORM, the application declares a place with a member of type `GeoPoint`, and asks for the spatial index
with `[Index]`:

```csharp
[Entity]
public interface IShop {
  [PrimaryKey, Auto] Guid Id { get; set; }
  [Size(100)] string Name { get; set; }
  [Index] GeoPoint Location { get; set; }
}

var shop = session.NewEntity<IShop>();
shop.Name = "Pike Place";
shop.Location = new GeoPoint(47.6097, -122.3422);
await session.SaveChangesAsync();
```

`GeoPoint(Latitude, Longitude)` is in `Mic.Common`: a point in degrees of WGS 84, the system of GPS and every
map, SRID 4326. The column is the server's geographic type, and the value travels to and from it as well-known
binary. On PostgreSQL the column is PostGIS `geography`; the upgrade creates the extension in the database before
the first point column, so the server has to have PostGIS installed. SQLite has no spatial type: the column holds
the binary, and the point reads back as it was written.

An index over a `GeoPoint` member is the server's spatial index, whatever it is called there: GiST on
PostgreSQL, a spatial index on SQL Server, MySQL and Oracle. MySQL wants the column `NOT NULL` for it; a nullable
point gets a warning there and no index.

## Distance queries

A query measures with `GeoFunctions.Distance(point, other)`, in `Mic.Linq`: the distance between two points along
the Earth's surface, in metres. It goes in a filter, an ordering or a projection:

```csharp
var here = new GeoPoint(47.6062, -122.3321);

// Within ten kilometres.
var near = await session.EntitySet<IShop>()
  .Where(s => GeoFunctions.Distance(s.Location, here) < 10_000)
  .ToListAsync();

// The three nearest, with the distance.
var nearest = await session.EntitySet<IShop>()
  .OrderBy(s => GeoFunctions.Distance(s.Location, here))
  .Take(3)
  .Select(s => new { s.Name, Metres = GeoFunctions.Distance(s.Location, here) })
  .ToListAsync();
```

The translator writes the call as the server's own function: `ST_Distance` on PostgreSQL, `STDistance` on SQL
Server, `ST_Distance_Sphere` on MySQL, `SDO_GEOM.SDO_DISTANCE` on Oracle. Each server measures on its own figure
of the Earth, a spheroid or a sphere; the figures agree within a fraction of a percent. A filter on the distance
uses the spatial index where the server's planner finds it fit. In [QueryMaker](querymaker.md) the same function
is `GeoDistance`.

## In memory and on SQLite

`GeoFunctions.Distance` is an ordinary method too: it runs over a [cached entity](data-cache.md) and in plain code,
as the great-circle distance on a sphere, within a fraction of a percent of the server's.

SQLite has no spatial type and no function over a point's bytes. The SQLite provider gives every connection it
opens a function of its own, `mic_geo_distance`, which runs the same method, so a query that measures a distance
returns there what the method returns in memory. No index serves it: SQLite calls the function for each row. A
tool that opens the same file through another connection does not have the function.
