using Mic.Linq;
using Mic.Orm;

namespace Mic.Tests.Orm.Db;

// A COLUMN THE DATABASE COMPUTES out of a JSON document: read like any member, filtered on, never written, and the
// document's own discriminator.
public abstract partial class DmlTests {
  [TestMethod]
  public async Task ComputedColumn_ReadsAValueOutOfTheDocument() {
    await using var writer = NewSession();
    var arrow = writer.NewEntity<Dml.IMarked>();
    arrow.Id = Guid.NewGuid();
    arrow.Marker = new Dml.Arrow { X = 3, Length = 10 };
    var label = writer.NewEntity<Dml.IMarked>();
    label.Id = Guid.NewGuid();
    label.Marker = new Dml.Label { X = 7, Text = "hi" };
    await writer.SaveChangesAsync();

    // ----- the server computed the columns out of what the serializer wrote ----------------------------------------
    await using var reader = NewSession();
    var loaded = await reader.GetEntityAsync<Dml.IMarked>(arrow.Id);
    Assert.AreEqual("arrow", loaded.Kind);
    Assert.AreEqual(3, loaded.X);
    Assert.IsInstanceOfType<Dml.Arrow>(loaded.Marker, "read as its own type, from the discriminator inside the document");
    Assert.AreEqual(10, ((Dml.Arrow)loaded.Marker).Length);

    // ----- a query filters on the column, which the index covers ------------------------------------------------
    Assert.AreEqual(1, await reader.EntitySet<Dml.IMarked>().CountAsync(m => m.Kind == "label" && m.Id == label.Id));
    Assert.AreEqual(0, await reader.EntitySet<Dml.IMarked>().CountAsync(m => m.Kind == "arrow" && m.Id == label.Id));

    // ----- the column follows the document ----------------------------------------------------------------------
    loaded.Marker = new Dml.Label { X = 9, Text = "now" };
    await reader.SaveChangesAsync();
    await using var again = NewSession();
    var changed = await again.GetEntityAsync<Dml.IMarked>(arrow.Id);
    Assert.AreEqual("label", changed.Kind);
    Assert.AreEqual(9, changed.X);
    Assert.AreEqual("label", await ProbeScalarAsync<string>(
      $"SELECT {Db.Quote("Kind")} FROM {Db.GetTableRef("Marked")} WHERE {Db.Quote("Id")} = @id", ("id", arrow.Id)));
  }

  [TestMethod]
  public async Task ComputedColumn_IsTheServersExpressionOverTheRow() {
    await using var writer = NewSession();
    var line = writer.NewEntity<Dml.ILineItem>();
    line.Id = Guid.NewGuid();
    line.Price = 7;
    line.Qty = 3;
    await writer.SaveChangesAsync();

    await using var reader = NewSession();
    var loaded = await reader.GetEntityAsync<Dml.ILineItem>(line.Id);
    Assert.AreEqual(21, loaded.Total);
    Assert.AreEqual(1, await reader.EntitySet<Dml.ILineItem>().CountAsync(l => l.Total == 21 && l.Id == line.Id));

    // ----- the column follows the row --------------------------------------------------------------------------
    loaded.Qty = 5;
    await reader.SaveChangesAsync();
    await using var again = NewSession();
    Assert.AreEqual(35, (await again.GetEntityAsync<Dml.ILineItem>(line.Id)).Total);
  }
}
