using Mic.Orm;
using Mic.Orm.Db;
using Mic.Orm.Db.Model.Upgrades;
using Mic.Orm.Runtime;

namespace Mic.Tests.Orm.Db;

// THE SAVE PATH, ANNOUNCED. One of the three moments only exists against a real database: `SavedChanges` needs a
// statement to have got through. The other two are covered without a server in `Mic.Tests.Orm`.
public abstract partial class DmlTests {
  // EVERY TEST HERE TAKES ITS OWN APPLICATION over the shared database. Handlers hooked on an entity are part of the
  // MODEL and are never unsubscribed, so hooking one on the shared application would hook it for every test after
  // this file — see `DmlTests.NewApp`.
  [TestMethod]
  public async Task SavedChanges_SeesTheServersNewKey() {
    var app = NewApp();
    var entity = app.Model!.FindEntity("Ticket")!;
    var id = entity.GetMember("Id");

    var seenId = -1L;
    var seenStatus = (EntityStatus?)null;
    var seenStatusBeforeSave = (EntityStatus?)null;
    entity.Events.SavedChanges += record => {
      seenId = (long)record.GetValue(id)!;
      seenStatus = record.Status;
      seenStatusBeforeSave = record.StatusBeforeSave;
    };

    var session = NewContext(app).OpenSession();
    var ticket = session.NewEntity<Dml.ITicket>();
    ticket.Title = "Broken";
    Assert.AreEqual(0, ticket.Id, "the key does not exist before the insert");
    await session.SaveChangesAsync();

    Assert.AreNotEqual(-1, seenId, "the handler should have run");
    Assert.AreEqual(ticket.Id, seenId, "and seen the real identity, not a placeholder");
    Assert.AreEqual(EntityStatus.Loaded, seenStatus);
    Assert.AreEqual(EntityStatus.New, seenStatusBeforeSave, "with the record still saying what it was doing");
  }

  // A handler's own changes come after the save took its set: they wait for the next save, and that one writes them.
  [TestMethod]
  public async Task SavedChangesWrites_ArePendingForTheNextSave() {
    var app = NewApp();
    var entity = app.Model!.FindEntity("Ticket")!;
    var title = entity.GetMember("Title");
    Dml.ITicket? added = null;
    entity.Events.SavedChanges += record => {
      if (added != null)
        return;
      record.SetValue(title, "rewritten");
      added = record.Session!.NewEntity<Dml.ITicket>();
      added.Title = "added";
    };

    var session = (EntitySession)NewContext(app).OpenSession();
    var ticket = session.NewEntity<Dml.ITicket>();
    ticket.Title = "original";
    await session.SaveChangesAsync();
    Assert.AreEqual(EntityStatus.Modified, EntityHelper.GetRecord(ticket).Status);
    Assert.AreEqual(EntityStatus.New, EntityHelper.GetRecord(added!).Status);
    Assert.AreEqual(2, session.GetEntityTracker().Modified.Count);
    Assert.AreEqual("original", await GetStoredTitleAsync(ticket.Id), "the first save wrote what it had taken");

    await session.SaveChangesAsync();
    Assert.AreEqual(0, session.GetEntityTracker().Modified.Count);
    Assert.AreEqual("rewritten", await GetStoredTitleAsync(ticket.Id));
    Assert.AreEqual("added", await GetStoredTitleAsync(added!.Id));

    // ----- local functions
    Task<string?> GetStoredTitleAsync(long id) => ProbeScalarAsync<string>(
      $"SELECT {Db.Quote("Title")} FROM {Db.GetTableRef("Ticket")} WHERE {Db.Quote("Id")} = @id", ("id", id));
  }

  [TestMethod]
  public async Task SavedChanges_TellsAnInsertFromAnUpdate() {
    var app = NewApp();
    // The whole reason `StatusBeforeSave` is kept: by now every saved record is Loaded, and "was this new?" is the
    // first thing an audit or a publish asks.
    var entity = app.Model!.FindEntity("Ticket")!;
    var seen = new List<EntityStatus?>();
    entity.Events.SavedChanges += record => seen.Add(record.StatusBeforeSave);

    var session = NewContext(app).OpenSession();
    var ticket = session.NewEntity<Dml.ITicket>();
    ticket.Title = "First";
    await session.SaveChangesAsync();

    ticket.Title = "Second";
    await session.SaveChangesAsync();

    CollectionAssert.AreEqual(new EntityStatus?[] { EntityStatus.New, EntityStatus.Modified }, seen);
  }

  [TestMethod]
  public async Task SaveMoments_ArriveInTheSavesOrder() {
    var app = NewApp();
    var entity = app.Model!.FindEntity("Ticket")!;
    var order = new List<string>();
    entity.Events.SavingChanges += _ => order.Add("saving");
    entity.Events.SavedChanges += _ => order.Add("saved");
    entity.Events.SaveChangesAborted += _ => order.Add("aborted");

    var session = NewContext(app).OpenSession();
    session.NewEntity<Dml.ITicket>().Title = "One";
    await session.SaveChangesAsync();

    CollectionAssert.AreEqual(new[] { "saving", "saved" }, order,
      "and never 'aborted' on a save that got through — got: " + string.Join(", ", order));
  }

  [TestMethod]
  public async Task SavingChangesWrites_AreInTheStatement() {
    var app = NewApp();
    // Announced before the statements are planned, so the write is part of the INSERT rather than a second round trip.
    var entity = app.Model!.FindEntity("Ticket")!;
    var title = entity.GetMember("Title");
    entity.Events.SavingChanges += record => record.SetValue(title, "rewritten");

    var session = NewContext(app).OpenSession();
    var ticket = session.NewEntity<Dml.ITicket>();
    ticket.Title = "original";
    await session.SaveChangesAsync();

    Assert.AreEqual(1, session.GetLastCommands().Count, "one statement, not two");
    var stored = await ProbeScalarAsync<string>(
      $"SELECT {Db.Quote("Title")} FROM {Db.GetTableRef("Ticket")} WHERE {Db.Quote("Id")} = @id", ("id", ticket.Id));
    Assert.AreEqual("rewritten", stored);
  }

  // ----- the application's own events, on the side that succeeds ---------------------------------------------------

  [TestMethod]
  public async Task ASaveThatGOTTHROUGHIsAnnouncedOnce() {
    var app = NewApp();
    var order = new List<string>();
    app.Events.SavingChanges += _ => order.Add("saving");
    app.Events.SavedChanges += _ => order.Add("saved");
    app.Events.SaveChangesAborted += _ => order.Add("aborted");

    var session = NewContext(app).OpenSession();
    for (var i = 0; i < 3; i++)
      session.NewEntity<Dml.ITicket>().Title = $"T{i}";
    await session.SaveChangesAsync();

    CollectionAssert.AreEqual(new[] { "saving", "saved" }, order,
      "once per SAVE, whatever the record count — got: " + string.Join(", ", order));
  }

  [TestMethod]
  public async Task SavedChanges_SeesASettledSession() {
    var app = NewApp();
    var pending = -1;
    app.Events.SavedChanges += session => pending = session.GetEntityTracker().Modified.Count;

    var writer = NewContext(app).OpenSession();
    writer.NewEntity<Dml.ITicket>().Title = "Broken";
    await writer.SaveChangesAsync();

    Assert.AreEqual(0, pending, "nothing left pending by the time the application is told");
  }

  [TestMethod]
  public async Task EveryStatement_IsAnnounced() {
    var app = NewApp();
    var commands = new List<DataCommandSnapshot>();
    app.Events.CommandExecuted += (_, command) => commands.Add(command);

    var session = NewContext(app).OpenSession();
    session.NewEntity<Dml.ITicket>().Title = "Broken";
    await session.SaveChangesAsync();

    CollectionAssert.AreEqual(session.GetLastCommands().ToArray(), commands,
      "the same snapshots the session keeps — they are taken once and handed on");
    Assert.IsTrue(commands.All(c => !string.IsNullOrEmpty(c.Sql)));
  }

  // ----- Loaded, which only a real row can produce -----------------------------------------------------------------

  /// <summary>LOADED is announced ONCE PER RECORD, for a row that genuinely ARRIVED.</summary>
  /// <remarks>Three methods, one rule seen from its three sides: a row read is an arrival, a row read AGAIN is not
  /// (the second read finds the record in the map and DISCARDS what it read, so a handler that decrypts a column or
  /// warms a cache must not be asked twice), and a record SETTLING after a save is a status change rather than an
  /// arrival at all.</remarks>
  [TestMethod]
  public async Task Loaded_AnnouncedOncePerArrivedRow() {
    var app = NewApp();
    var entity = app.Model!.FindEntity("Ticket")!;
    var title = entity.GetMember("Title");
    var loaded = new List<string>();
    entity.Events.Loaded += record => loaded.Add((string)record.GetValue(title)!);

    // ----- SAVING a new record announces nothing: settling is not an arrival -------------------------------------------
    var writer = NewContext(app).OpenSession();
    var ticket = writer.NewEntity<Dml.ITicket>();
    ticket.Title = "Broken";
    await writer.SaveChangesAsync();

    Assert.AreEqual(0, loaded.Count);

    // ----- while READING it in a second session — where it must come from the database — does -----------------------------
    var reader = NewContext(app).OpenSession();
    var read = await reader.GetEntityAsync<Dml.ITicket>(ticket.Id);

    Assert.IsNotNull(read);
    CollectionAssert.AreEqual(new[] { "Broken" }, loaded);

    // ----- and reading it AGAIN does not ----------------------------------------------------------------------------------
    await reader.GetEntityAsync<Dml.ITicket>(ticket.Id);
    Assert.AreEqual(1, loaded.Count, "one record, one arrival");
  }

  [TestMethod]
  public async Task BatchedInsert_AnnouncesEveryRecord() {
    var app = NewApp();
    // The rows of one bulk insert share a statement; each of them is still announced as saved.
    var entity = app.Model!.FindEntity("Ticket")!;
    var saved = 0;
    entity.Events.SavedChanges += _ => saved++;

    var session = NewContext(app).OpenSession();
    for (var i = 0; i < 5; i++)
      session.NewEntity<Dml.ITicket>().Title = $"T{i}";
    await session.SaveChangesAsync();

    Assert.AreEqual(5, saved);
  }
}
