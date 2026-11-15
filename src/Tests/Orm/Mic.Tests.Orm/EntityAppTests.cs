using Mic.Orm;

namespace Mic.Tests.Orm;

// The DECLARATION tier — what a developer writes. These tests exercise it in the shape it will really be used: a small
// application whose modules register interfaces, rather than objects assembled field by field.
//
// What is being pinned down is that declaration is COLLECTION, not building: registering an entity records a Type and
// examines nothing about it. An interface with no properties is registered as happily as a real one, because deciding
// whether it is a usable entity is the builder's job, and the builder is what can say so with a useful diagnostic.
[TestClass]
public class EntityAppTests {
  private interface IPerson { }
  private interface IAddress { }
  private interface INothingAtAll { }

  private sealed class PeopleModule : EntityModule {
    public PeopleModule() : base("People", new Version(1, 0)) {
      RegisterEntities(typeof(IPerson), typeof(IAddress));
    }
  }

  private sealed class PayrollModule : EntityModule {
    public PayrollModule() : base("Payroll", new Version(1, 0)) {
      Requires<PeopleModule>();
    }
  }

  private static EntityApp NewApp() {
    var app = new EntityApp("TestApp", new Version(1, 0), new EntityAppSettings { DefaultSchema = "app" });
    app.AddModule(new PeopleModule(), "people");
    app.AddModule(new PayrollModule());
    return app;
  }

  // ----- declaration RECORDS; it does not judge -------------------------------------------------------------------------
  //
  // Ten methods, each two lines over the same two modules. What they say together is the tier's whole rule: everything
  // here is collection, and every DECISION about what was collected belongs to the builder.
  [TestMethod]
  public void Module_RecordsRegistrationsAndRequirements() {
    var module = new PeopleModule();
    CollectionAssert.AreEqual(new[] { typeof(IPerson), typeof(IAddress) }, module.EntityTypes.ToArray());

    // …including an interface with nothing in it. Whether that is a usable entity is decided by the builder, which is
    // what can say so with a location and a reason.
    module.RegisterEntities(typeof(INothingAtAll));
    Assert.IsTrue(module.EntityTypes.Contains(typeof(INothingAtAll)));

    // The one thing it does refuse is registering the same type twice, which cannot be meant.
    Assert.ThrowsExactly<InvalidOperationException>(() => module.RegisterEntities(typeof(IPerson)));

    // ----- and a dependency is recorded as a TYPE ---------------------------------------------------------------------
    //
    // Nothing has looked for a PeopleModule — that is the validation pass's job.
    var payroll = new PayrollModule();
    CollectionAssert.AreEqual(new[] { typeof(PeopleModule) }, payroll.Dependencies.ToArray());

    payroll.Requires<PeopleModule>();
    Assert.AreEqual(1, payroll.Dependencies.Count, "saying it twice says it once");
  }

  [TestMethod]
  public void AddingAModule_AssignsSchemaAndApplication() {
    var app = NewApp();
    var people = app.FindModule("People")!;
    Assert.AreEqual("people", people.Schema);
    Assert.AreSame(app, people.App);

    // ----- a module that named no schema leaves it NULL for the app default -------------------------------------------
    //
    // Resolved when the model is built rather than baked in at declaration, so it can still be reassigned before Init().
    Assert.IsNull(app.FindModule("Payroll")!.Schema);
    Assert.AreEqual("app", app.Settings.DefaultSchema);

    people.Schema = "hr";          // what an application does before Init()
    Assert.AreEqual("hr", people.Schema);

    // ----- while a module belongs to ONE application, and a name to one module -----------------------------------------
    var otherApp = new EntityApp("Other", new Version(1, 0));
    Assert.ThrowsExactly<InvalidOperationException>(() => otherApp.AddModule(people));
    Assert.ThrowsExactly<InvalidOperationException>(() => app.AddModule(new PeopleModule()));
  }
}
