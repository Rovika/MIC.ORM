using Mic.Linq;
using Mic.Orm;
using Mic.Orm.Db.Provider;
using Mic.Orm.Logging;

namespace Mic.Tests.Orm.Db;

// THE SESSION THROUGH ITS INTERFACE. An application holds an IEntitySession; everything it does beyond the interface
// goes through the extensions in Mic.Orm, which reach the class inside. This file uses nothing from Mic.Orm.Runtime.
public abstract partial class DmlTests {
  [TestMethod]
  public async Task SessionExtensions_ReachTheClassThroughTheInterface() {
    var tag = Guid.NewGuid().ToString("N")[..8];
    IEntitySession session = NewContext(App).OpenSession();

    // ----- the options --------------------------------------------------------------------------------------------
    Assert.AreEqual(EntitySessionOptions.None, session.GetOptions());
    session.SetOptions(EntitySessionOptions.DisableSmartLoad);
    Assert.IsTrue(session.GetOptions().IsSet(EntitySessionOptions.DisableSmartLoad));
    Assert.IsFalse(session.GetOptions().IsSet(EntitySessionOptions.DisableBatchMode));

    // ----- what the session holds: a copy takes the columns, not the key and not the references ----------------------
    var home = session.NewEntity<Dml.IAddress>();
    home.Id = Guid.NewGuid();
    home.City = tag + "-city";
    var person = session.NewEntity<Dml.IPerson>();
    person.Id = Guid.NewGuid();
    person.LastName = tag;
    person.Rank = 7;
    person.Address = home;
    var copy = session.NewCopyOf(person);
    copy.Id = Guid.NewGuid();
    Assert.AreEqual(tag, copy.LastName);
    Assert.AreEqual(7, copy.Rank);
    Assert.IsNull(copy.Address, "references are not copied");
    Assert.AreEqual(3, session.GetChangeCount());
    Assert.AreEqual(3, session.GetChangedRecords().Count);

    // ----- the transaction, through the interface -----------------------------------------------------------------
    Assert.IsFalse(session.HasOpenTransaction());
    await session.BeginTransactionAsync();
    Assert.IsTrue(session.HasOpenTransaction());
    await session.SaveChangesAsync();
    await session.CommitTransactionAsync();
    Assert.IsFalse(session.HasOpenTransaction());
    Assert.AreEqual(0, session.GetChangeCount());

    // ----- what it ran ----------------------------------------------------------------------------------------------
    Assert.IsTrue(session.GetLastCommands().Count >= 1);
    Assert.AreSame(session.GetLastCommands()[^1], session.GetLastCommand());

    // ----- the pages, the key, the log --------------------------------------------------------------------------------
    var reader = NewContext(App).OpenSession();
    Assert.AreEqual(1, reader.GetEntities<Dml.IPerson>(p => p.LastName, take: 1).Count);
    var byRank = await reader.GetEntitiesAsync<Dml.IPerson>(p => p.Rank, descending: true, take: 2);
    Assert.AreEqual(2, byRank.Count);
    Assert.IsTrue(byRank[0].Rank >= byRank[1].Rank);
    var key = reader.CreatePrimaryKey<Dml.IPerson>(person.Id);
    Assert.AreEqual(tag, (await reader.GetEntityAsync<Dml.IPerson>(key)).LastName);
    reader.LogMessage("session extensions {0}", tag);
    var mode = reader.Context.LogMode;
    reader.EnableLog(false);
    Assert.AreEqual(LogMode.None, reader.Context.LogMode);
    reader.EnableLog();
    Assert.AreEqual(LogMode.Details, reader.Context.LogMode);
    reader.Context.LogMode = mode;

    // ----- a sequence, by name and by definition ----------------------------------------------------------------------
    if (!Db.Provider.Supports(DbFeatures.Sequences))
      return;
    var first = reader.GetSequenceNextValue("mic_receipt_numbers");
    var second = await reader.GetSequenceNextValueAsync(new SequenceDefinition("mic_receipt_numbers", 1, 1, typeof(long)));
    Assert.IsTrue(second > first, $"{second} after {first}");
  }
}
