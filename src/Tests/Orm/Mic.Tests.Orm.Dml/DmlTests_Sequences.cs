using Mic.Linq;
using Mic.Orm;
using Mic.Orm.Db;
using Mic.Orm.Db.Provider;

namespace Mic.Tests.Orm.Db;

// A SEQUENCE MEMBER: the save draws the next values from the server's sequence, one statement per sequence, and puts
// them on the new records before their insert — so the insert, bulk or single, carries the number like any value.
public abstract partial class DmlTests {
  [TestMethod]
  public async Task Sequence_NumbersNewRowsAtTheSave() {
    if (!Db.Provider.Supports(DbFeatures.Sequences))
      Assert.Inconclusive($"{Db.Provider.ServerKind} has no sequences; the member is an ordinary column.");
    var tag = "seq-" + Guid.NewGuid().ToString("N")[..6];

    // ----- three in one save: one draw of three values, one multi-row insert ---------------------------------------
    await using var session = NewSession();
    var first = session.NewEntity<Dml.IReceipt>();
    first.Note = tag + "-1";
    var second = session.NewEntity<Dml.IReceipt>();
    second.Note = tag + "-2";
    var third = session.NewEntity<Dml.IReceipt>();
    third.Note = tag + "-3";
    Assert.AreEqual(0, first.Number, "nothing assigned yet");
    await session.SaveChangesAsync();

    Assert.IsTrue(first.Number >= 1000, $"the sequence starts at 1000; drew {first.Number}");
    Assert.AreEqual(first.Number + 10, second.Number, "the declared step");
    Assert.AreEqual(second.Number + 10, third.Number, "drawn in order");
    var commands = session.LastCommands;
    Assert.AreEqual(1, commands.Count, "the save was one insert; the draw ran before it as a request of its own");
    StringAssert.Contains(commands[0].Sql, "INSERT");

    // ----- the numbers are what the server holds -----------------------------------------------------------------
    Assert.AreEqual(second.Number, await ProbeScalarAsync<long>(
      $"SELECT {Db.Quote("Number")} FROM {Db.GetTableRef("Receipt")} WHERE {Db.Quote("Id")} = @id", ("id", second.Id)));

    // ----- another save draws past them; a number the application assigned is kept --------------------------------
    await using var later = NewSession();
    var fourth = later.NewEntity<Dml.IReceipt>();
    fourth.Note = tag + "-4";
    var assigned = later.NewEntity<Dml.IReceipt>();
    assigned.Note = tag + "-5";
    assigned.Number = 900_000_000;
    await later.SaveChangesAsync();
    Assert.IsTrue(fourth.Number > third.Number && (fourth.Number - third.Number) % 10 == 0,
      $"{fourth.Number} after {third.Number}, on the step");
    Assert.AreEqual(900_000_000, assigned.Number, "an assigned number is not drawn over");
    Assert.AreEqual(900_000_000, await ProbeScalarAsync<long>(
      $"SELECT {Db.Quote("Number")} FROM {Db.GetTableRef("Receipt")} WHERE {Db.Quote("Id")} = @id", ("id", assigned.Id)));
  }
}

// A SEQUENCE PRIMARY KEY: the parent's key is drawn and on the record before any statement is built, so a child
// referencing the new parent gets the key copied into its foreign key when its own insert is built, like a key the
// client made — no slot marker, which only a key the server makes during the batch needs.
public abstract partial class DmlTests {
  [TestMethod]
  public async Task Sequence_KeysAParentBeforeItsChildrenAreWritten() {
    if (!Db.Provider.Supports(DbFeatures.Sequences))
      Assert.Inconclusive($"{Db.Provider.ServerKind} has no sequences; the member is an ordinary column.");
    var tag = "ship-" + Guid.NewGuid().ToString("N")[..6];
    var shipmentKey = App.Model!.FindEntity("ShipmentItem")!.GetMember("Shipment").ReferenceInfo!.FromKey.Members[0].Member.Name;
    string CountItemsSql() =>
      $"SELECT COUNT(*) FROM {Db.GetTableRef("ShipmentItem")} WHERE {Db.Quote(shipmentKey)} = @id";

    // ----- a parent and two children in one save: the children go as one multi-row insert where the vendor has it --
    await using var session = NewSession();
    var shipment = session.NewEntity<Dml.IShipment>();
    shipment.Note = tag;
    var first = session.NewEntity<Dml.IShipmentItem>();
    first.Shipment = shipment;
    first.Sku = tag + "-a";
    var second = session.NewEntity<Dml.IShipmentItem>();
    second.Shipment = shipment;
    second.Sku = tag + "-b";
    Assert.AreEqual(0, shipment.Id, "nothing drawn yet");
    await session.SaveChangesAsync();

    Assert.IsTrue(shipment.Id > 0, $"the key was drawn: {shipment.Id}");
    Assert.AreEqual(2L, Convert.ToInt64(await ProbeScalarAsync<object>(CountItemsSql(), ("id", shipment.Id))),
      "both children name the drawn key");

    // ----- a parent and one child, the single-row path, in a session of their own -------------------------------
    await using var later = NewSession();
    var next = later.NewEntity<Dml.IShipment>();
    next.Note = tag + "-2";
    var only = later.NewEntity<Dml.IShipmentItem>();
    only.Shipment = next;
    only.Sku = tag + "-c";
    await later.SaveChangesAsync();
    Assert.IsTrue(next.Id > shipment.Id, "drawn after the first");
    Assert.AreEqual(1L, Convert.ToInt64(await ProbeScalarAsync<object>(CountItemsSql(), ("id", next.Id))));

    // ----- read back through the reference: the parent is found by the key the sequence made ---------------------
    await using var reader = NewSession();
    var items = await reader.EntitySet<Dml.IShipmentItem>().Where(i => i.Shipment.Id == shipment.Id).ToListAsync();
    Assert.AreEqual(2, items.Count);
    Assert.AreEqual(tag, items[0].Shipment.Note);
  }
}

// DRAWING BY HAND: an application that needs a number outside a save asks the session, one value or several in one
// round trip, and the save after it continues from where the draw left the sequence.
public abstract partial class DmlTests {
  [TestMethod]
  public async Task Sequence_IsDrawnFromByTheApplication() {
    await using var session = NewSession();
    if (!Db.Provider.Supports(DbFeatures.Sequences)) {
      var refused = await Assert.ThrowsExactlyAsync<NotSupportedException>(
        () => session.GetNextSequenceValueAsync("mic_receipt_numbers"));
      StringAssert.Contains(refused.Message, "mic_receipt_numbers");
      return;
    }
    var one = await session.GetNextSequenceValueAsync("mic_receipt_numbers");
    var three = await session.GetNextSequenceValuesAsync("mic_receipt_numbers", 3);
    Assert.AreEqual(3, three.Count);
    Assert.AreEqual(one + 10, three[0], "the draw of three starts where the draw of one left off");
    Assert.AreEqual(three[0] + 10, three[1]);
    Assert.AreEqual(three[1] + 10, three[2]);

    // ----- a save after the draws numbers its row past them -----------------------------------------------------
    var receipt = session.NewEntity<Dml.IReceipt>();
    receipt.Note = "drawn-" + Guid.NewGuid().ToString("N")[..6];
    await session.SaveChangesAsync();
    Assert.AreEqual(three[2] + 10, receipt.Number);

    // ----- a name the model lacks is refused by name ------------------------------------------------------------
    var unknown = await Assert.ThrowsExactlyAsync<ArgumentException>(
      () => session.GetNextSequenceValueAsync("mic_nothing"));
    StringAssert.Contains(unknown.Message, "mic_nothing");
  }
}
