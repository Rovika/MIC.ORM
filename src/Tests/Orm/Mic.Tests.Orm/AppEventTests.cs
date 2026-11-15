using Mic.Orm;
using Mic.Orm.Db;
using Mic.Orm.Db.Postgres;
using Mic.Orm.Model;
using Mic.Orm.Runtime;

namespace Mic.Tests.Orm;

// WHAT AN APPLICATION WATCHES ACROSS ITS TYPES — once per session, once per save, rather than once per row. The half
// that needs no server: everything up to and including a save that fails because the database is not there. The
// successful-save side lives in `Mic.Tests.Orm.Db`.
[TestClass]
public class AppEventTests {
  public interface IOrder {
    [PrimaryKey, Auto(AutoType.NewGuid)] Guid Id { get; set; }
    [Size(20)] string? Number { get; set; }
  }

  const string NeverConnects = "Host=nowhere.invalid;Database=none;Username=none;Password=none";

  static EntityApp NewApp(bool connected = false) {
    var app = new EntityApp("Sales", new Version(1, 0), new EntityAppSettings { DefaultSchema = "app" });
    var module = new EntityModule("M", new Version(1, 0));
    module.RegisterEntities(typeof(IOrder));
    app.AddModule(module);
    app.TryInit();
    Assert.IsTrue(app.Initialized, app.Report());
    if (connected)
      AppInitHelper.CreateDatabase(app, new DbSettings(new PostgresProvider(), NeverConnects));
    else
      app.WithSqlite();
    return app;
  }

  // ----- sessions -------------------------------------------------------------------------------------------------

  [TestMethod]
  public void Session_IsAnnouncedOnceFullyBuilt() {
    var app = NewApp(connected: true);
    var opened = new List<EntitySession>();
    Database? seen = null;
    app.Events.SessionOpened += session => { opened.Add(session); seen = session.Database; };

    var first = app.OpenSession();
    var second = app.OpenSession();

    CollectionAssert.AreEqual(new[] { first, second }, opened);
    Assert.IsNotNull(seen, "the database is resolved before anyone is told");
  }

  // ----- saves ----------------------------------------------------------------------------------------------------

  [TestMethod]
  public void Save_IsAnnouncedBeforeAndAfter() {
    var app = NewApp(connected: true);
    var order = new List<string>();
    var announced = 0;
    var aborted = 0;
    var saved = 0;
    app.Events.SavingChanges += _ => { announced++; order.Add("app"); };
    app.Events.SaveChangesAborted += _ => aborted++;
    app.Events.SavedChanges += _ => saved++;
    app.Model!.FindEntity("Order")!.Events.SavingChanges += _ => order.Add("record");

    // ----- with NOTHING pending, nothing is announced ----------------------------------------------------------------
    //
    // Otherwise a transaction log fills up with empty transactions.
    app.OpenSession().SaveChanges();
    Assert.AreEqual(0, announced);

    // ----- and with something pending, the APPLICATION is told before the records are ---------------------------------
    //
    // One announcement that a save has begun, before the per-record pass that may add to it.
    var session = app.OpenSession();
    session.NewEntity<IOrder>();
    Assert.ThrowsExactly<DataAccessException>(() => session.SaveChanges());

    CollectionAssert.AreEqual(new[] { "app", "record" }, order);

    // ----- while a save that THREW announces aborted, once -------------------------------------------------------------
    Assert.AreEqual(1, aborted, "once for the save, whatever the record count");
    Assert.AreEqual(0, saved);
  }

  [TestMethod]
  public void ARefusedVALIDATIONIsNotAnAbortedSave() {
    // Nothing was attempted: the save never started, so there is nothing to say it ended.
    var app = new EntityApp("Strict", new Version(1, 0), new EntityAppSettings { DefaultSchema = "app" });
    var module = new EntityModule("M", new Version(1, 0));
    module.RegisterEntities(typeof(IRequiredRow));
    app.AddModule(module);
    app.TryInit();
    app.WithSqlite();
    var errors = 0;
    var aborted = 0;
    app.Events.Error += (_, _) => errors++;
    app.Events.SaveChangesAborted += _ => aborted++;

    var session = app.OpenSession();
    session.NewEntity<IRequiredRow>();

    Assert.ThrowsExactly<ClientFaultException>(() => session.SaveChanges());
    Assert.AreEqual(0, errors, "a refusal is an answer, not an incident");
    Assert.AreEqual(1, aborted, "the save had begun, so it is announced as having ended");
  }

  public interface IRequiredRow {
    [PrimaryKey, Auto(AutoType.NewGuid)] Guid Id { get; set; }
    [Size(20)] string Name { get; set; }
  }

  // ----- failures -------------------------------------------------------------------------------------------------

  [TestMethod]
  public void ADataFailureIsAnnouncedWithItsContext() {
    var app = NewApp(connected: true);
    OperationContext? seenContext = null;
    Exception? seenError = null;
    app.Events.Error += (context, error) => { seenContext = context; seenError = error; };

    var operation = new OperationContext(app);
    var session = operation.OpenSession();
    session.NewEntity<IOrder>();
    var thrown = Assert.ThrowsExactly<DataAccessException>(() => session.SaveChanges());

    // The handler saw it, and the caller still caught it — the handlers run on the way past, not instead of.
    Assert.AreSame(operation, seenContext);
    Assert.AreSame(thrown, seenError, "the handler sees the very exception the caller will catch");
  }

  // ----- databases ---------------------------------------------------------------------------------------------

  [TestMethod]
  public void MissingDatabase_IsAnnouncedBeforeRefusal() {
    var app = NewApp();
    var asked = new List<string>();
    app.DatabaseEvents.DatabaseMissing += (_, name) => asked.Add(name);

    var context = new OperationContext(app) { DatabaseAlias = "tenant-7" };
    var ex = Assert.ThrowsExactly<InvalidOperationException>(() => app.DataAccess.GetDatabase(context));

    // Announced, and then refused anyway — a handler that resolves nothing leaves the failure exactly as it was.
    CollectionAssert.AreEqual(new[] { "tenant-7" }, asked);
    StringAssert.Contains(ex.Message, "tenant-7");

    // ----- while a REGISTERED one is found without anybody being asked -------------------------------------------------
    var connected = NewApp(connected: true);
    var askedAgain = 0;
    connected.DatabaseEvents.DatabaseMissing += (_, _) => askedAgain++;

    _ = connected.DataAccess.GetDatabase(new OperationContext(connected));

    Assert.AreEqual(0, askedAgain, "the hit path must not pay for the miss path");
  }

  [TestMethod]
  public void AHandlerCanREGISTERTheMissingDatabase() {
    // The point of the event: the registry looks AGAIN once the handlers have run, so an application that resolves a
    // tenant on first use turns the failure into a hit. Its own application, because the handler CHANGES it.
    var app = NewApp();
    app.DatabaseEvents.DatabaseMissing += (a, name) => {
      AppInitHelper.CreateDatabase(a, new DbSettings(new PostgresProvider(), NeverConnects, databaseAlias: name));
    };

    var context = new OperationContext(app) { DatabaseAlias = "tenant-7" };
    var source = app.DataAccess.GetDatabase(context);

    Assert.AreEqual("tenant-7", source.Alias);
  }

  [TestMethod]
  public void ASessionAsksForItsDatabase_AndOneMissIsHandledAtATime() {
    // The session resolves its database through the service, so the handler runs for a session opened on a new alias,
    // not only for a direct lookup.
    var app = NewApp();
    var handled = 0;
    app.DatabaseEvents.DatabaseMissing += (a, name) => {
      Interlocked.Increment(ref handled);
      Thread.Sleep(50); // long enough for the other sessions to arrive at the miss
      AppInitHelper.CreateDatabase(a, new DbSettings(new PostgresProvider(), NeverConnects, databaseAlias: name));
    };

    var session = new OperationContext(app) { DatabaseAlias = "tenant-7" }.OpenSession();
    Assert.AreEqual("tenant-7", ((EntitySession)session).Database.Alias);
    Assert.AreEqual(1, handled);

    // ----- and FOUR sessions opened at once for one new alias connect it ONCE ---------------------------------------------
    //
    // The first takes the miss; the others wait on it and find the database registered.
    var sessions = Enumerable.Range(0, 4)
      .Select(_ => Task.Run(() => new OperationContext(app) { DatabaseAlias = "tenant-8" }.OpenSession()))
      .ToArray();
    Task.WaitAll(sessions);

    Assert.AreEqual(2, handled, "one miss for tenant-8, whoever got there first");
    var databases = sessions.Select(s => ((EntitySession)s.Result).Database).Distinct().ToList();
    Assert.AreEqual(1, databases.Count, "every session got the one database the handler registered");

    // ----- a session on an alias no handler resolves is refused with the aliases there are ------------------------------
    var ex = Assert.ThrowsExactly<InvalidOperationException>(
      () => new OperationContext(NewApp(connected: true)) { DatabaseAlias = "tenant-9" }.OpenSession());
    StringAssert.Contains(ex.Message, "tenant-9");
    StringAssert.Contains(ex.Message, "Registered: (default)");
  }
}
