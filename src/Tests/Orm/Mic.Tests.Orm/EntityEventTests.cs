using Mic.Orm;
using Mic.Orm.Db;
using Mic.Orm.Db.Postgres;
using Mic.Orm.Logging;
using Mic.Orm.Model;
using Mic.Orm.Runtime;

namespace Mic.Tests.Orm;

// WHERE AN APPLICATION PUTS ITS OWN BEHAVIOUR. The chain end to end: a module hooks an entity's event when the model
// is constructed, and the ORM fires it while the application runs. Nothing here touches a database.
[TestClass]
public class EntityEventTests {
  public interface IOrder {
    [PrimaryKey, Auto(AutoType.NewGuid)] Guid Id { get; set; }

    // NULLABLE, so these tests can save a bare entity: validation runs inside `SaveChangesAsync`, and a required
    // member left empty would refuse the save before it reached the part being tested.
    [Size(20)] string? Number { get; set; }
    [Size(20)] string? Status { get; set; }
  }

  /// <summary>A module that gives every new order a status — the case the `New` event exists for: a default the
  /// application COMPUTES, which no attribute can declare.</summary>
  /// <remarks>It resolves the MEMBER once, while hooking, and the handler then writes through it. That is the pattern
  /// hooking a finished model is for: the lookup by name happens once at startup rather than per record.</remarks>
  class OrderModule : EntityModule {
    public OrderModule() : base("Orders", new Version(1, 0)) => RegisterEntities(typeof(IOrder));

    public EntityAppModel? SawModel;
    public int Created;

    public override void OnModelConstructed(EntityAppModel model) {
      SawModel = model;
      var order = model.FindEntity("Order")!;
      var status = order.GetMember("Status");
      order.Events.New += record => {
        Created++;
        record.SetValue(status, "Draft");
      };
    }
  }

  class CountingService : IServiceInit {
    public int Inits;
    public EntityAppModel? SawModel;

    public void Init(EntityApp app) {
      Inits++;
      SawModel = app.Model;
    }
  }

  static EntityApp NewApp(out OrderModule module, CountingService? service = null) {
    var app = new EntityApp("Sales", new Version(1, 0), new EntityAppSettings { DefaultSchema = "app" });
    app.AddModule(module = new OrderModule());
    if (service != null)
      app.RegisterService(service);
    app.TryInit();
    app.WithSqlite();
    return app;
  }

  // ----- the hook -------------------------------------------------------------------------------------------------

  [TestMethod]
  public void Module_IsToldWhenTheModelIsBuilt() {
    var app = NewApp(out var module);
    Assert.IsTrue(app.Initialized, app.Report());
    Assert.AreSame(app.Model, module.SawModel, "the module should get the model this application then runs on");

    // ----- and it is FINISHED ---------------------------------------------------------------------------------------
    //
    // The whole argument for hooking HERE rather than mid-build: every derived fact is present, so a handler can be
    // hooked against a model it can also read — and can resolve its members once instead of per record.
    var order = module.SawModel!.FindEntity("Order")!;
    Assert.IsNotNull(order.PrimaryKey, "the key is worked out by now");
    Assert.IsNotNull(order.GetMember("Status").GetValueFunc,
      "and the accessors are assigned — the model is finished, not half-built");
    Assert.AreEqual(1, order.AutoOnCreateMembers.Count, "and the derived member lists are filled");

    // ----- a BROKEN model is not announced at all --------------------------------------------------------------------
    //
    // A handler hooking a member the builder abandoned would throw over the top of the diagnostics saying why.
    var broken = new EntityApp("Broken", new Version(1, 0), new EntityAppSettings { DefaultSchema = "app" });
    var keyless = new KeylessModule();
    broken.AddModule(keyless);
    broken.TryInit();

    Assert.IsFalse(broken.Initialized, "this model was supposed to be broken");
    Assert.IsNull(keyless.SawModel, "a failed build must not run application code");
  }

  interface IKeylessRow {
    string Name { get; set; }
  }

  class KeylessModule : EntityModule {
    public KeylessModule() : base("Keyless", new Version(1, 0)) => RegisterEntities(typeof(IKeylessRow));
    public EntityAppModel? SawModel;
    public override void OnModelConstructed(EntityAppModel model) => SawModel = model;
  }

  // ----- services -------------------------------------------------------------------------------------------------

  [TestMethod]
  public void Service_IsToldOfTheModelExactlyOnce() {
    // Services are registered before there is a model, so this is the only moment one can key anything by entity type.
    var service = new CountingService();
    var app = NewApp(out _, service);
    Assert.AreEqual(1, service.Inits);
    Assert.AreSame(app.Model, service.SawModel, "and the model is PUBLISHED by then, not merely built");

    // ----- one OBJECT is one service, however many interfaces it was registered under ---------------------------------
    var shared = new CountingService();
    var twice = new EntityApp("Sales", new Version(1, 0), new EntityAppSettings { DefaultSchema = "app" });
    twice.AddModule(new OrderModule());
    twice.RegisterService<IServiceInit>(shared);
    twice.RegisterService(shared);
    twice.TryInit();

    Assert.AreEqual(1, shared.Inits);

    // ----- and a service that wants nothing is not asked anything ------------------------------------------------------
    //
    // The defaults implement none of this, and registering an ordinary service must stay free.
    Assert.IsNotNull(app.GetService<ITimeService>());
    Assert.IsTrue(app.Initialized);
  }

  // ----- when a hook throws ---------------------------------------------------------------------------------------
  //
  // These are somebody else's code running inside startup. What comes out has to be a REPORT saying which one failed,
  // not an exception from the middle of `Init` that its caller cannot place.

  class ThrowingModule(string name) : EntityModule(name, new Version(1, 0)) {
    public override void OnModelConstructed(EntityAppModel model) => throw new InvalidOperationException("boom");
  }

  class ThrowingService : IServiceInit {
    public void Init(EntityApp app) => throw new InvalidOperationException("service boom");
  }

  static EntityApp SalesApp() {
    var app = new EntityApp("Sales", new Version(1, 0), new EntityAppSettings { DefaultSchema = "app" });
    app.AddModule(new OrderModule());
    return app;
  }

  // Five methods, one rule: whatever threw and wherever it was hooked, the answer is a REPORT and an application that
  // does not claim to be ready.
  [TestMethod]
  public void Hook_ThatThrowsIsReported() {
    // A HOOK THAT THREW GOES TO THE LOG, not to a report. It is a fault in APPLICATION CODE — a thing that happened
    // during startup — where the model's own report is about the model, and the model here built perfectly well. So a
    // test that wants to see it listens, which is what `CollectingLog` arranges.

    // ----- a MODULE hook -------------------------------------------------------------------------------------------
    var module = SalesApp().CollectingLog();
    module.AddModule(new ThrowingModule("Bad"));

    Assert.IsFalse(module.TryInit(), "a hook that threw must not leave an application claiming to be ready");
    Assert.IsFalse(module.Initialized);
    Assert.IsFalse(module.Model!.BuildReport.HasErrors, "the MODEL was fine; the application's own code was not");
    StringAssert.Contains(module.Report(), "boom");
    StringAssert.Contains(module.Report(), "ThrowingModule",
      "the exception is kept, and its stack is what names the hook that threw");

    // …and the EXCEPTION is kept with the entry. A message alone loses the stack, which is the line that actually says
    // what went wrong.
    Assert.IsInstanceOfType<InvalidOperationException>(
      module.StartupErrors().OfType<ErrorLogEntry>().Single().Error);

    // ----- a SERVICE, reported the same way -------------------------------------------------------------------------
    var service = SalesApp().CollectingLog();
    service.RegisterService<IServiceInit>(new ThrowingService());

    Assert.IsFalse(service.TryInit());
    Assert.IsTrue(service.StartupErrors().Any(e => e.ToString().Contains("service boom")), service.Report());

    // ----- an OVERRIDE on the application itself --------------------------------------------------------------------
    var overridden = new ThrowingApp();
    overridden.CollectingLog();
    Assert.IsFalse(overridden.TryInit());
    Assert.IsTrue(overridden.StartupErrors().Any(e => e.ToString().Contains("OnModelConstructed")),
      overridden.Report());

    // ----- and the FIRST failure ends the whole pass -----------------------------------------------------------------
    //
    // There is no value in going on: the hooks after it were to run on an application being wired up, and wiring it up
    // is what has just failed.
    var twice = SalesApp().CollectingLog();
    twice.AddModule(new ThrowingModule("First"));
    twice.AddModule(new ThrowingModule("Second"));

    Assert.IsFalse(twice.TryInit());
    Assert.AreEqual(1, twice.StartupErrors().Count, twice.Report());
  }

  [TestMethod]
  public void Connecting_AfterAFailedInitThrowsTheReason() {
    // `Init` reports and `ConnectToAsync` throws, because connecting is not only a startup act: a multi-tenant
    // application connects a tenant's database mid-request, and a caller there has one thing to do with a failure.
    // What it throws is the fault that CAUSED the failed init, carrying the original exception — not "not
    // initialised", which would tell the caller only that they should have looked somewhere else.
    var app = new ThrowingApp();
    app.TryInit();

    var ex = Assert.ThrowsExactly<InvalidOperationException>(() => app.ConnectToAsync(
      new DbSettings(new PostgresProvider(), NeverConnects)).GetAwaiter().GetResult());

    StringAssert.Contains(ex.Message, "did not initialise");
    StringAssert.Contains(ex.Message, "override boom", "the reason, not a pointer to it");
    Assert.IsNotNull(ex.InnerException, "and the exception that actually threw, for its stack");
    Assert.AreEqual("override boom", ex.InnerException!.Message);
  }

  class ThrowingApp : EntityApp {
    public ThrowingApp() : base("Overridden", new Version(1, 0), new EntityAppSettings { DefaultSchema = "app" }) =>
      AddModule(new OrderModule());

    protected override void OnModelConstructed() => throw new InvalidOperationException("override boom");
  }

  // ----- the event ------------------------------------------------------------------------------------------------

  [TestMethod]
  public void NewEntity_ReachesTheHandlersInTurn() {
    var app = NewApp(out var module);
    var entity = app.Model!.FindEntity("Order")!;
    EntityRecord? seen = null;
    entity.Events.New += record => seen = record;

    var session = new EntitySession(new OperationContext(app));
    var order = session.NewEntity<IOrder>();

    Assert.AreEqual(1, module.Created);
    Assert.AreEqual("Draft", order.Status, "the module handler's write should be on the entity");

    // ----- and what the handler MET was a finished record ------------------------------------------------------------
    //
    // Fired LAST in NewEntity: New, keyed, and already in the save set — so what a handler writes is part of the same
    // INSERT rather than a second statement.
    Assert.IsNotNull(seen);
    Assert.AreEqual(EntityStatus.New, seen!.Status);
    Assert.AreNotEqual(Guid.Empty, seen.GetValue(entity.GetMember("Id")), "the [Auto] key is minted first");
    Assert.AreEqual(1, session.GetEntityTracker().Modified.Count, "and the record is already pending, so the write joins the insert");
    Assert.AreSame(EntityHelper.GetRecord(order), seen);

    // ----- handlers run in the order they were hooked ------------------------------------------------------------------
    //
    // Its own application, because the assertion is about what the LAST write left behind.
    var ordered = NewApp(out var orderedModule);
    var orderedEntity = ordered.Model!.FindEntity("Order")!;
    var status = orderedEntity.GetMember("Status");
    orderedEntity.Events.New += record => record.SetValue(status, "Submitted");   // after the module's

    var second = new EntitySession(new OperationContext(ordered)).NewEntity<IOrder>();

    Assert.AreEqual(1, orderedModule.Created, "the module's handler still ran");
    Assert.AreEqual("Submitted", second.Status, "and the later one saw its write and overwrote it");
  }

  // ----- the rest of the record's life -----------------------------------------------------------------------------

  /// <summary>A record made and then made to look like one that was READ — which is what these need, and what nothing
  /// disconnected can otherwise produce.</summary>
  static EntityRecord AsLoaded(EntitySession session, out IOrder entity) {
    entity = session.NewEntity<IOrder>();
    var record = EntityHelper.GetRecord(entity);
    record.Status = EntityStatus.Loaded;
    record.AcceptChanges();
    return record;
  }

  [TestMethod]
  public void Modified_FiresOnTheTransitionForAReadRow() {
    // An application asking to be told a row went dirty wants telling once, not per member — the second write adds
    // nothing it did not already know.
    var app = NewApp(out _);
    var order = app.Model!.FindEntity("Order")!;
    var number = order.GetMember("Number");
    var status = order.GetMember("Status");
    var modified = 0;
    order.Events.Modified += _ => modified++;

    var session = new EntitySession(new OperationContext(app));
    var record = AsLoaded(session, out _);

    record.SetValue(number, "N-1");
    record.SetValue(status, "Open");

    Assert.AreEqual(1, modified, "two writes, one transition");
    Assert.AreEqual(EntityStatus.Modified, record.Status);

    // ----- while a NEW record being written to announces nothing ------------------------------------------------------
    //
    // It is already in the save set and stays an INSERT — being written to twice does not make it an update.
    session.NewEntity<IOrder>().Number = "N-1";
    Assert.AreEqual(1, modified, "there was no transition to announce");
  }

  [TestMethod]
  public void Deleting_FiresOnceBeforeTheStatusChanges() {
    var app = NewApp(out _);
    var order = app.Model!.FindEntity("Order")!;
    var seen = (EntityStatus?)null;
    var deleting = 0;
    order.Events.Deleting += record => { deleting++; seen = record.Status; };

    var session = new EntitySession(new OperationContext(app));
    var record = AsLoaded(session, out var entity);

    session.DeleteEntity(entity);

    Assert.AreEqual(EntityStatus.Loaded, seen, "the handler should meet the record as it still is");
    Assert.AreEqual(EntityStatus.Deleted, record.Status);

    // ----- and deleting it AGAIN announces nothing further -------------------------------------------------------------
    session.DeleteEntity(entity);
    Assert.AreEqual(1, deleting);

    // ----- as does deleting what was never saved -----------------------------------------------------------------------
    //
    // A New entity is simply forgotten — there is no row to delete, so there is no deletion to announce.
    var made = session.NewEntity<IOrder>();
    var pending = session.GetEntityTracker().Modified.Count;
    session.DeleteEntity(made);

    Assert.AreEqual(1, deleting);
    Assert.AreEqual(pending - 1, session.GetEntityTracker().Modified.Count, "and it is gone from the save set");
  }

  [TestMethod]
  public void AHandlerCanREFUSEADeletion() {
    // Its own application: the handler throws, so anything hooked after it on the same model would never be reached.
    var app = NewApp(out _);
    app.Model!.FindEntity("Order")!.Events.Deleting +=
      _ => throw new InvalidOperationException("orders are never deleted");

    var session = new EntitySession(new OperationContext(app));
    var record = AsLoaded(session, out var entity);

    Assert.ThrowsExactly<InvalidOperationException>(() => session.DeleteEntity(entity));
    Assert.AreEqual(EntityStatus.Loaded, record.Status, "and the record is untouched, since nothing had changed yet");
  }

  // ----- the save path, as far as it goes without a server ---------------------------------------------------------
  //
  // `SavingChanges` fires before anything is planned and `SaveChangesAborted` fires when the save throws — so an
  // application pointed at a database that is not there exercises both, with no server involved. The rest of the save
  // path, `SavedChanges`, needs a real insert and lives in `Mic.Tests.Orm.Db`.

  const string NeverConnects = "Host=nowhere.invalid;Database=none;Username=none;Password=none";

  static EntityApp UnreachableApp(out OrderModule module) {
    var app = NewApp(out module);
    AppInitHelper.CreateDatabase(app, new DbSettings(new PostgresProvider(), NeverConnects));
    return app;
  }

  [TestMethod]
  public void SavingChanges_FiresBeforeThePlanAbortedWithoutServer() {
    var app = UnreachableApp(out _);
    var order = app.Model!.FindEntity("Order")!;
    var number = order.GetMember("Number");
    var seenStatus = (EntityStatus?)null;
    var aborted = 0;
    var saved = 0;
    order.Events.SavingChanges += record => {
      seenStatus = record.Status;
      record.SetValue(number, "N-1");           // the last chance to write, and it lands in the same INSERT
    };
    order.Events.SaveChangesAborted += _ => aborted++;
    order.Events.SavedChanges += _ => saved++;

    var session = new EntitySession(new OperationContext(app));
    var entity = session.NewEntity<IOrder>();

    // Every assertion below is made THROUGH a throwing save: the handlers run on the way past, not instead of, so the
    // exception reaching the caller is not a separate claim.
    Assert.ThrowsExactly<DataAccessException>(() => session.SaveChanges());

    Assert.AreEqual(EntityStatus.New, seenStatus, "the record should still be New when it is announced");
    Assert.AreEqual("N-1", entity.Number);

    // ----- and a save that THREW announces aborted, and keeps its records ---------------------------------------------
    Assert.AreEqual(1, aborted);
    Assert.AreEqual(0, saved, "a save that threw announces one or the other, never both");
    Assert.AreEqual(1, session.GetEntityTracker().Modified.Count, "and the records stay pending, so the caller can fix and retry");
  }

  [TestMethod]
  public void HandlerCreatedEntity_JoinsTheSameSave() {
    // The reason the announcement is a snapshot and the stamping runs after it: creating a child row is exactly what
    // one does here, and it must be part of THIS save. Its own application, because the handler adds a record
    // to whatever save it is announced for.
    var app = UnreachableApp(out _);
    var order = app.Model!.FindEntity("Order")!;
    EntitySession? session = null;
    var announced = 0;
    order.Events.SavingChanges += _ => {
      announced++;
      if (announced == 1)
        session!.NewEntity<IOrder>();           // a second row, made from inside the announcement
    };

    session = new EntitySession(new OperationContext(app));
    session.NewEntity<IOrder>();
    Assert.ThrowsExactly<DataAccessException>(() => session.SaveChanges());

    Assert.AreEqual(1, announced, "the pass is over a snapshot — what a handler creates is not itself announced");
    Assert.AreEqual(2, session.GetEntityTracker().Modified.Count, "but it IS in the save set, so it is written with the rest");
  }

  [TestMethod]
  public void TwoApplications_DoNotShareHandlers() {
    // The events hang off the METADATA, and each application builds its own — so a second application in one process,
    // or the next test, cannot be reached by handlers hooked here.
    var first = NewApp(out var firstModule);
    var second = NewApp(out var secondModule);

    new EntitySession(new OperationContext(first)).NewEntity<IOrder>();

    Assert.AreEqual(1, firstModule.Created);
    Assert.AreEqual(0, secondModule.Created);
    Assert.AreNotSame(first.Model!.FindEntity("Order")!.Events, second.Model!.FindEntity("Order")!.Events);
  }
}
