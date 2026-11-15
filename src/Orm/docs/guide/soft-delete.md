# Soft delete extension

Applications that keep business records rarely remove them: an order stays for the books, a user for the audit
trail, a record its owner may want back. A deleted record is hidden rather than gone: a mark on the row, a
condition on every query over the table, and a delete that is really an update.

MIC ORM's `SoftDeleteExtension` does the three. An entity bound to a deleted mark keeps its rows: `DeleteEntity`
becomes an update that writes the mark at the save, and every read hides the marked rows. The extension serves a
new application and an existing database alike; what differs is how it finds the member that holds the mark.

## A new application

The ORM ships the marker `ISoftDeleteEntity`, a nullable `DeletedOn`. The application inherits it on the entities
that keep their rows and adds the extension through its parameterless constructor:

```csharp
using Mic.Orm.Extensions.SoftDelete;

[Entity]
public interface IMemo : ISoftDeleteEntity {
  [PrimaryKey, Auto] Guid Id { get; set; }
  …
}

// In the application's constructor:
AddExtension(new SoftDeleteExtension());

// In a session: an update, not a delete.
session.DeleteEntity(memo);
await session.SaveChangesAsync();

// In a session with the extension disabled: the deleted rows read, one restored, one removed for good.
var admin = context.OpenSession();
admin.DisableExtension<SoftDeleteExtension>();
var deleted = await admin.EntitySet<IMemo>().Where(m => m.DeletedOn != null).ToListAsync();
deleted[0].DeletedOn = null;
admin.DeleteEntity(deleted[1]);
await admin.SaveChangesAsync();
```

`DeletedOn` is null while the row lives; a delete writes the application's clock's time into it, and every read
carries `DeletedOn IS NULL`.

## An existing database

A legacy database holds its mark in a column of its own naming, an `IsDeleted` bit or a `RemovedOn` date, and
the entities cannot inherit a marker. The extension's other constructor takes the member's name:

```csharp
AddExtension(new SoftDeleteExtension("IsDeleted"));
```

Every entity with a member of that name is bound. What the mark is comes from the member's type: a nullable
`DateTime` is null while the row lives and takes the clock's time at the delete; a `bool` is false while the row
lives and becomes true. A member of any other type, a status code, does not build. Two attributes handle the
exceptions: `[DeletedMark]` on a member binds it whatever its name, and `[DeletedMark(false)]` on the member of
the configured name excludes the entity. An attribute wins over the name and over the marker, and the name
replaces the marker: with it set, `ISoftDeleteEntity` is not consulted.

## What the extension does

The delete is turned into an update when the session saves: the record marked for deletion is set modified and
the mark written, and within that session the entity still reads as it did. A fresh session does not see the row:
a query, a load by key and a smart load carry the condition. The filter reads nothing from the operation context,
so a cached entity keeps one block for every caller. A job that must see the deleted rows disables the extension
on its session: it reads them, restores one by clearing the mark and saving, and removes one for good with
`DeleteEntity`, a `DELETE` there. See [Disabling an extension on a
session](app-extensions.md#disabling-an-extension-on-a-session).
