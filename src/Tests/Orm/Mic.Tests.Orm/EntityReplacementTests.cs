using Mic.Orm;
using Mic.Orm.Db;
using Mic.Orm.Db.Postgres;
using Mic.Orm.Runtime;

namespace Mic.Tests.Orm;

// WHAT MAKES A MODULE REUSABLE. A module that must reference the application's own type cannot declare it, so it
// declares a PLACEHOLDER, references that, and the application says what really stands there. Modelled here on the
// scenario the feature exists for: two modules written independently, assembled by a third party.
[TestClass]
public class EntityReplacementTests {
  // ----- what a module author writes ------------------------------------------------------------------------------

  /// <summary>The login module's stub for "whoever the application calls a user". Empty on purpose.</summary>
  public interface IUserStub {
    [PrimaryKey, Auto(AutoType.NewGuid)] Guid Id { get; set; }
  }

  public interface ILogin {
    [PrimaryKey, Auto(AutoType.NewGuid)] Guid Id { get; set; }
    [Size(30)] string UserName { get; set; }
    IUserStub User { get; set; }          // typed by the STUB — the module has nothing else to type it by
  }

  public class LoginModule : EntityModule {
    public LoginModule() : base("Login", new Version(1, 0)) =>
      RegisterEntities(typeof(ILogin), typeof(IUserStub));
  }

  /// <summary>A person module, written by somebody who has never heard of the login module.</summary>
  public interface IPerson {
    [PrimaryKey, Auto(AutoType.NewGuid)] Guid Id { get; set; }
    [Size(50)] string LastName { get; set; }
  }

  public class PeopleModule : EntityModule {
    public PeopleModule() : base("People", new Version(1, 0)) => RegisterEntities(typeof(IPerson));
  }

  // ----- what the application assembling them writes --------------------------------------------------------------

  /// <summary>The real thing: a person, extended, and a user as far as the login module is concerned.</summary>
  public interface IPersonExt : IPerson, IUserStub {
    [Size(20)] string? Nickname { get; set; }
  }

  public class AppModule : EntityModule {
    public AppModule() : base("App", new Version(1, 0)) => RegisterEntities(typeof(IPersonExt));
  }

  static EntityApp Assembled(Action<EntityApp>? extra = null) {
    var app = new EntityApp("Assembled", new Version(1, 0), new EntityAppSettings { DefaultSchema = "app" });
    app.AddModule(new LoginModule());
    app.AddModule(new PeopleModule());
    app.AddModule(new AppModule());
    app.ReplaceEntity<IUserStub, IPersonExt>();
    app.ReplaceEntity<IPerson, IPersonExt>();
    extra?.Invoke(app);
    app.TryInit();
    app.WithSqlite();
    return app;
  }

  // ----- the model that comes out ---------------------------------------------------------------------------------

  // ----- ONE assembled application, followed all the way down -----------------------------------------------------------
  //
  // Seven methods, each assembling the same three modules to ask one question. The walk is the point: model, then
  // lookups, then the module's own reference, then the mapping, then the runtime — a replacement has to hold at every
  // one of them or it holds at none.
  [TestMethod]
  public void Placeholder_IsReplacedEverywhereItStood() {
    var app = Assembled();
    Assert.IsTrue(app.Initialized, app.Report());

    // ----- it is NOT in the model -----------------------------------------------------------------------------------
    //
    // The whole point: a stub never existed as storage, and leaving it in would create an empty table per stub.
    var names = app.Model!.Entities.Select(e => e.Name).ToArray();
    CollectionAssert.DoesNotContain(names, "UserStub");
    CollectionAssert.DoesNotContain(names, "Person", "the replaced type is gone too, not only the empty stub");
    CollectionAssert.Contains(names, "PersonExt");
    CollectionAssert.Contains(names, "Login");

    // ----- but ASKING for it gives the real entity --------------------------------------------------------------------
    //
    // What makes the feature small: a module's own lookups keep working, and so does every reference it declared.
    Assert.AreEqual("PersonExt", app.Model.FindEntity(typeof(IUserStub))!.Name);
    Assert.AreEqual("PersonExt", app.Model.FindEntity(typeof(IPerson))!.Name);
    Assert.AreEqual("PersonExt", app.Model.FindEntity(typeof(IPersonExt))!.Name);

    // …including the MODULE's own reference. `ILogin.User` is typed `IUserStub`, and the module cannot know what it
    // will become.
    Assert.AreEqual("PersonExt", app.Model.FindEntity("Login")!.GetMember("User").ReferenceInfo!.TargetEntity.Name);

    // ----- and the replacement keeps the members of BOTH its parents ----------------------------------------------------
    var person = app.Model.FindEntity("PersonExt")!;
    Assert.IsNotNull(person.FindMember("LastName"), "from IPerson");
    Assert.IsNotNull(person.FindMember("Nickname"), "its own");
    Assert.IsNotNull(person.PrimaryKey, "and the key its parents declared");

    // ----- no TABLE is mapped for a placeholder ---------------------------------------------------------------------------
    AppInitHelper.CreateDatabase(app, new DbSettings(new PostgresProvider(), "Host=nowhere.invalid;Database=x"));
    var tables = app.MappedModel().Tables.Select(t => t.Name).ToArray();
    CollectionAssert.DoesNotContain(tables, "UserStub");
    CollectionAssert.DoesNotContain(tables, "Person");
    CollectionAssert.Contains(tables, "PersonExt");

    // ----- and the RUNTIME works through it too ----------------------------------------------------------------------------
    var session = app.OpenSession();
    var made = session.NewEntity<IPersonExt>();
    var login = session.NewEntity<ILogin>();
    login.UserName = "johnd";
    login.User = made;                  // the assignment derivation exists for

    Assert.AreSame(made, login.User, "and it reads back as the same instance");
    Assert.AreEqual("PersonExt", EntityHelper.GetEntityInfo(login.User).Name);
  }

  // ----- chains ---------------------------------------------------------------------------------------------------

  public interface IMiddleLink : IUserStub {
    [Size(10)] string? Tag { get; set; }
  }

  public interface IFinalTarget : IMiddleLink {
    [Size(10)] string? Extra { get; set; }
  }

  public class ChainModule : EntityModule {
    public ChainModule() : base("Chain", new Version(1, 0)) =>
      RegisterEntities(typeof(IUserStub), typeof(IMiddleLink), typeof(IFinalTarget), typeof(ILogin));
  }

  [TestMethod]
  public void Chain_ResolvesToTheTypeAtItsEnd() {
    // A stub replaced by a type that is itself replaced: the LAST one is the real entity, and a chain that took the
    // first hop instead would be the worst failure available.
    var app = new EntityApp("Chained", new Version(1, 0), new EntityAppSettings { DefaultSchema = "app" });
    app.AddModule(new ChainModule());
    app.ReplaceEntity<IUserStub, IMiddleLink>();
    app.ReplaceEntity<IMiddleLink, IFinalTarget>();
    app.TryInit();

    Assert.IsTrue(app.Initialized, app.Report());
    Assert.AreEqual("FinalTarget", app.Model!.FindEntity(typeof(IUserStub))!.Name);
    Assert.AreEqual("FinalTarget", app.Model.FindEntity(typeof(IMiddleLink))!.Name);

    var names = app.Model.Entities.Select(e => e.Name).ToArray();
    CollectionAssert.DoesNotContain(names, "UserStub");
    CollectionAssert.DoesNotContain(names, "MiddleLink");
    CollectionAssert.Contains(names, "FinalTarget");

    // ----- while a CYCLE cannot be BUILT ---------------------------------------------------------------------------
    //
    // The derivation rule makes one unconstructible: a cycle needs A to derive from B and B from A, which no pair of
    // interfaces can do. The builder still walks defensively, but derivation is what actually stops it.
    var cyclic = new EntityApp("Cyclic", new Version(1, 0), new EntityAppSettings { DefaultSchema = "app" });
    cyclic.AddModule(new ChainModule());
    cyclic.ReplaceEntity(typeof(IMiddleLink), typeof(IFinalTarget));
    cyclic.ReplaceEntity(typeof(IFinalTarget), typeof(IMiddleLink));

    Assert.IsFalse(cyclic.TryInit());
    Assert.IsTrue(cyclic.Diagnostics().Any(d => d.Message.Contains("form a cycle")), cyclic.Report());
  }

  // ----- what is refused ------------------------------------------------------------------------------------------

  public interface IBystander {
    [PrimaryKey, Auto(AutoType.NewGuid)] Guid Id { get; set; }
  }

  // EVERY refusal is a DIAGNOSTIC — `EntityApp` records a replacement and checks none of it. Some of these need no
  // model (a type is an interface, a type is not itself) and could have been caught at the call, but one place to
  // look and one report to read beats a rule split across two.
  [TestMethod]
  public void Replacement_ThatCannotMeanAnythingIsRefused() {
    // ----- one that does not DERIVE from the placeholder --------------------------------------------------------------
    //
    // BOTH have to be registered for derivation to be the fault that is reported: an unregistered replacement is a
    // different and earlier complaint, and the builder says the first true thing it finds.
    var wrong = new EntityApp("Wrong", new Version(1, 0), new EntityAppSettings { DefaultSchema = "app" });
    var bothModule = new EntityModule("Both", new Version(1, 0));
    bothModule.RegisterEntities(typeof(IUserStub), typeof(IBystander));
    wrong.AddModule(bothModule);
    wrong.ReplaceEntity(typeof(IUserStub), typeof(IBystander));
    Assert.IsFalse(wrong.TryInit());
    Assert.IsTrue(wrong.Diagnostics().Any(d => d.Message.Contains("must derive from")), wrong.Report());

    // ----- one type replaced TWICE --------------------------------------------------------------------------------------
    var twice = new EntityApp("Twice", new Version(1, 0), new EntityAppSettings { DefaultSchema = "app" });
    twice.AddModule(new ChainModule());
    twice.ReplaceEntity(typeof(IUserStub), typeof(IMiddleLink));
    twice.ReplaceEntity(typeof(IUserStub), typeof(IFinalTarget));
    Assert.IsFalse(twice.TryInit());
    Assert.IsTrue(twice.Diagnostics().Any(d => d.Message.Contains("already replaced")), twice.Report());

    // ----- and a type replaced by ITSELF -----------------------------------------------------------------------------------
    var silly = new EntityApp("Silly", new Version(1, 0), new EntityAppSettings { DefaultSchema = "app" });
    silly.AddModule(new ChainModule());
    silly.ReplaceEntity(typeof(IUserStub), typeof(IUserStub));
    Assert.IsFalse(silly.TryInit());
    Assert.IsTrue(silly.Diagnostics().Any(d => d.Message.Contains("cannot replace itself")), silly.Report());

    // ----- an UNREGISTERED placeholder ------------------------------------------------------------------------------
    //
    // The module still has to register what it declares, even though it is about to be replaced.
    var unregistered = new EntityApp("Unregistered", new Version(1, 0), new EntityAppSettings { DefaultSchema = "app" });
    var module = new EntityModule("M", new Version(1, 0));
    module.RegisterEntities(typeof(IPersonExt));
    unregistered.AddModule(module);
    unregistered.ReplaceEntity(typeof(IUserStub), typeof(IPersonExt));
    unregistered.TryInit();

    Assert.IsFalse(unregistered.Initialized);
    Assert.IsTrue(unregistered.Diagnostics().Any(d => d.Message.Contains("not a registered entity")),
      unregistered.Report());

    // ----- while an application that replaces NOTHING is untouched by any of it ---------------------------------------------
    var plain = new EntityApp("Plain", new Version(1, 0), new EntityAppSettings { DefaultSchema = "app" });
    plain.AddModule(new PeopleModule());
    plain.TryInit();

    Assert.IsTrue(plain.Initialized, plain.Report());
    Assert.AreEqual("Person", plain.Model!.FindEntity(typeof(IPerson))!.Name);
  }
}
