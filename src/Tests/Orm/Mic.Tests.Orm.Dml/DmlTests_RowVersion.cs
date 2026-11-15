using Mic.Orm;
using Mic.Orm.Db;
using Mic.Orm.Runtime;

namespace Mic.Tests.Orm.Db;

// THE ROW VERSION: an integer the ORM counts, matched on by every update and delete of the row and counted up by
// the update — so a save over a row somebody else changed since it was read finds no row and is refused as a
// concurrent update. Optimistic concurrency, for the collisions seconds apart; the row locks are for the ones
// milliseconds apart, and the two do not overlap.
public abstract partial class DmlTests {
  [TestMethod]
  public async Task RowVersion_RefusesAStaleSave() {
    var document = await NewDocumentAsync("ver-" + Guid.NewGuid().ToString("N")[..6], 1);

    // ----- two sessions read the same row; the first to save wins, the second is refused ---------------------------
    await using var first = NewSession();
    await using var second = NewSession();
    var mine = await first.GetEntityAsync<Dml.IDocument>(document);
    var theirs = await second.GetEntityAsync<Dml.IDocument>(document);
    Assert.AreEqual(0, mine.Version, "a new row starts at version 0");

    mine.Total = 10;
    await first.SaveChangesAsync();
    Assert.AreEqual(1, mine.Version, "the update counted the version up, on the record, with no read-back");
    var updateSql = first.LastCommands[^1].Sql;
    StringAssert.Contains(updateSql, Db.Quote("Version"), "the version is in the statement — set and matched on");
    Assert.AreEqual(2, updateSql.Split(Db.Quote("Version")).Length - 1, "once in the SET, once in the WHERE");

    theirs.Total = 20;
    var refused = await Assert.ThrowsExactlyAsync<DataAccessException>(() => second.SaveChangesAsync());
    Assert.AreEqual(DataAccessException.SubTypeConcurrentUpdate, refused.SubType);
    StringAssert.Contains(refused.Message, "Document");
    Assert.AreEqual(10, await ProbeScalarAsync<int>(
      $"SELECT {Db.Quote("Total")} FROM {Db.GetTableRef("Document")} WHERE {Db.Quote("Id")} = @id", ("id", document)),
      "the first save's value stands");

    // ----- a second save by the winner matches on the new version --------------------------------------------------
    mine.Total = 11;
    await first.SaveChangesAsync();
    Assert.AreEqual(2, mine.Version);

    // ----- a stale delete is the same conflict; a fresh one goes through ---------------------------------------------
    await using var third = NewSession();
    var stale = await third.GetEntityAsync<Dml.IDocument>(document);
    mine.Total = 12;
    await first.SaveChangesAsync();                 // version 3 now; `third` holds 2
    third.DeleteEntity(stale);
    var refusedDelete = await Assert.ThrowsExactlyAsync<DataAccessException>(() => third.SaveChangesAsync());
    Assert.AreEqual(DataAccessException.SubTypeConcurrentUpdate, refusedDelete.SubType);

    await using var fourth = NewSession();
    var fresh = await fourth.GetEntityAsync<Dml.IDocument>(document);
    Assert.AreEqual(3, fresh.Version);
    foreach (var line in fresh.Lines.ToList())
      fourth.DeleteEntity(line);
    fourth.DeleteEntity(fresh);
    await fourth.SaveChangesAsync();
    Assert.IsNull(await fourth.GetEntityAsync<Dml.IDocument>(document), "deleted, on the version it held");

    // ----- the version is the ORM's to count, never the application's to set --------------------------------------
    var secondDocument = await NewDocumentAsync("ver-" + Guid.NewGuid().ToString("N")[..6], 0);
    var loaded = await first.GetEntityAsync<Dml.IDocument>(secondDocument);
    var ex = Assert.ThrowsExactly<InvalidOperationException>(() => loaded.Version = 7);
    StringAssert.Contains(ex.Message, "row version");
  }
}
