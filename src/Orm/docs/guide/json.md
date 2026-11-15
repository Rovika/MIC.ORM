# JSON

Database servers store JSON documents in a column and query inside them. An application keeps data there that
has no fixed shape: a product's specification, a user's settings, a document that comes in several variants,
where a column per property does not fit and a serialized blob is opaque to the server.

MIC ORM maps such a member to the server's JSON type. The application reads and writes the object; the ORM
serializes it at the save and deserializes it at the read. A query reads inside the document by path, written as
the server's JSON function. A member declared as a base type holds any of its derived types, and a `[FromJson]`
member exposes one value of the document as a column the server computes, to filter, order and index on.

## A JSON member

```csharp
public record Dimensions(decimal Width, decimal Height, decimal Depth);

[Entity]
public interface IProduct {
  [PrimaryKey, Auto] Guid Id { get; set; }
  [Size(100)] string Name { get; set; }
  [Json] Dimensions? Size { get; set; }
  [Json] List<string>? Tags { get; set; }
}
```

The column is the server's JSON type where it has one, `jsonb` on PostgreSQL, and unlimited text otherwise: on
SQL Server before 2025 and on SQLite. There the ORM gives the column a check constraint that the text is JSON,
`ISJSON` or `json_valid`, so every server refuses a document that is not one. One such row would fail every
query that reads into the documents of the table.

A save writes a document whose member was assigned a different value:
`product.Size = product.Size with { Depth = 5 }`. A change inside the object is not looked for. The application
reports it:

```csharp
product.Tags!.Add("fragile");
EntityHelper.MarkModified(product, p => p.Tags);
```

The update writes the document, or for an object with few changes only the paths that changed, with the
server's own function. `MarkModified` takes any column member, as a lambda or by name, and puts its column into
the next update.

## The document as text

An endpoint hands a product's dimensions to its client as JSON and stores what the client sends back. Neither
step needs the object.

A loaded document stays the text the server sent until the member is read; the first read makes the object.
`EntityHelper.GetJson` returns the text and makes no object. `EntityHelper.SetJson` replaces the document from
text and marks the member as changed:

```csharp
string? json = EntityHelper.GetJson(product, p => p.Size);
EntityHelper.SetJson(product, p => p.Size, """{"Width":10,"Height":4,"Depth":5}""");
```

Both take the member by name as well: `EntityHelper.GetJson(product, "Size")`. `SetJson` does not check the text.
The server refuses what is not JSON, at the save.

A member that is only ever text is declared as a string: `[Json] string? Spec { get; set; }`. The column is the
same, and the application reads and writes the document as it is. Such a member takes no default string size; a
`[Size]` on it limits the text a save accepts.

## Typed documents

A JSON member declared as a base type holds any of a set of derived types. Which one a row holds is said by
another column of the row, named in `[Json(Discriminator = "…")]`, and the module maps the column's values to
the types:

```csharp
[Entity]
public interface IDrawing {
  [PrimaryKey, Auto] Guid Id { get; set; }
  ShapeKind Kind { get; set; }
  [Json(Discriminator = "Kind")] Shape Shape { get; set; }
}

// In the module:
MapJsonTypes<Shape>((ShapeKind.Circle, typeof(Circle)), (ShapeKind.Square, typeof(Square)));
```

The discriminator is a string, a number or an enum member, converted to the column's type. A row whose document
is of a type the column's value does not allow is refused at the save with a `ClientFault` of code
`JsonTypeMismatch`. A base type that carries the serializer's own `[JsonPolymorphic]` and `[JsonDerivedType]`
attributes needs no declaration: the discriminator is then inside the document.

## Querying inside the document

```csharp
// A property of the document is read by path, as the property's type: in a filter, an ordering, a projection.
var wide = await session.EntitySet<IProduct>()
  .Where(p => p.Size!.Width > 100 && p.Size.Depth < 20)
  .OrderByDescending(p => p.Size!.Height)
  .Select(p => new { p.Name, p.Size!.Height })
  .ToListAsync();

// A path the type does not name, or a document with no declared type.
var metric = await session.EntitySet<IProduct>()
  .Where(p => JsonFunctions.Value<string>(p.Size, "$.Unit") == "cm")
  .CountAsync();

// An array in the document: asked about, or spread as rows.
var fragile = await session.EntitySet<IProduct>().Where(p => p.Tags!.Contains("fragile")).ToListAsync();
var tagged = await session.EntitySet<IProduct>()
  .SelectMany(p => p.Tags!, (p, tag) => new { p.Name, Tag = tag })
  .ToListAsync();
```

The translator writes the read as the server's own JSON function, cast to the property's type:
`jsonb_path_query_first` on PostgreSQL, `JSON_VALUE` on SQL Server and Oracle, `JSON_EXTRACT` on MySQL,
`json_extract` on SQLite. A path that names nothing reads as null, as does a null document. `JsonFunctions.Value<T>`
is in `Mic.Linq`. An array spreads with `SelectMany`, and `Any`, `Count()` and `Contains` over it translate too.
Over a cached entity the same queries run in memory.

The read is computed per row. For a value filtered on often, the next section makes it an indexed column.

## A value of the document as a column

A value that queries filter and order on often, `Width` here, deserves a column of its own: the server can index
a column, not a path inside a document. In MIC ORM, the application declares the column with `[FromJson]`:

```csharp
[Json]
Dimensions? Size { get; set; }

[FromJson("Size", "$.Width"), Index]
decimal? Width { get; }
```

`[FromJson(member, path)]` declares a generated column: the server computes the value from the path of the
document. The column is a column of the table like any other: an index covers it, a query filters and orders on
it, `session.EntitySet<IProduct>().Where(p => p.Width > 10)`, and the server reads the index, not the documents.

`Stored` sets how the server keeps the value. By default the column is virtual: the server computes the value
from the document on each read, and it takes no space in the row. With `[FromJson("Size", "$.Width", Stored = true)]`
the server computes the value once, at insert and update, and writes it into the row; a read takes it as any
other column, at the price of the row's space. A stored column suits a value read often from a large document; a
virtual one suits a value read rarely, or documents that change often. Not every server has both kinds:
PostgreSQL before 18 stores every generated column, Oracle and SQLite compute every one on read; there the
server's kind wins over the declaration. See [Database objects](database-objects.md#computed-columns).
