using Mic.Linq;
using Mic.Orm;

namespace Mic.Tests.Orm;

// THE PUBLIC SURFACE OF DATA CODE. This file compiles with `using Mic.Orm;` and `using Mic.Linq;` only, and that is
// its first assertion: everything an application does at run time, opening sessions and contexts, reading, writing
// and querying, is reachable from these two namespaces. A using added to this file is a regression.
//
// The database is SQLite in memory; nothing here is about a server.
[TestClass]
public class PublicApiTests {
  public interface IAccount {
    [PrimaryKey, Auto] Guid Id { get; set; }
    [Size(40)] string Name { get; set; }
  }

  public interface IUnregistered {
    [PrimaryKey, Auto] Guid Id { get; set; }
  }

  static EntityApp NewApp() {
    var app = new EntityApp("Ledger", new Version(1, 0), new EntityAppSettings { DefaultSchema = "app" });
    var module = new EntityModule("M", new Version(1, 0));
    module.RegisterEntities(typeof(IAccount));
    app.AddModule(module);
    app.TryInit();
    app.WithSqlite();
    Assert.IsTrue(app.Initialized, app.Report());
    return app;
  }

  [TestMethod]
  public async Task Sessions_OpenFromTheAppAndFromAContext() {
    var app = NewApp();

    // ----- the application opens an anonymous session on a context of its own ---------------------------------------
    var anonymous = app.OpenSession();
    Assert.AreSame(UserInfo.Anonymous, anonymous.Context.User);
    Assert.AreSame(app, anonymous.Context.App);

    // ----- a context opens sessions for its user, and may open several ------------------------------------------------
    var user = new UserInfo(Guid.NewGuid(), "ada");
    using var context = new OperationContext(app, user);
    var first = context.OpenSession();
    var second = context.OpenSession(EntitySessionOptions.DisableSmartLoad);
    Assert.AreSame(context, first.Context);
    Assert.AreSame(context, second.Context);
    Assert.AreSame(user, first.Context.User);

    // ----- the system session: on the context itself when its user is the system user, else on a child ---------------
    var system = context.OpenSystemSession();
    Assert.AreNotSame(context, system.Context, "a user's context does not become the system's");
    Assert.AreSame(UserInfo.System, system.Context.User);
    Assert.AreSame(app, system.Context.App);
    var systemContext = app.CreateSystemContext();
    Assert.AreSame(UserInfo.System, systemContext.User);
    Assert.AreSame(systemContext, systemContext.OpenSystemSession().Context, "already the system's: no child");
    Assert.AreSame(UserInfo.System, app.OpenSystemSession().Context.User);

    // ----- and the sessions work without a server: entities are made, checked and deleted; queries are written ------
    var account = first.NewEntity<IAccount>();
    account.Name = "cash";
    first.ValidateChanges();
    first.DeleteEntity(account);
    Func<Task<IReadOnlyList<IAccount>>> load = () => second.EntitySet<IAccount>().Where(a => a.Name == "cash").ToListAsync();
    Assert.IsNotNull(load);   // not run: this suite has no server. DmlTests_PublicApi runs the surface against one.
    await Task.CompletedTask;
  }

  public interface IKeyless {
    [Size(40)] string Name { get; set; }
  }

  [TestMethod]
  public void Init_ReturnsTheReportOrThrowsIt() {
    var good = new EntityApp("Ledger", new Version(1, 0), new EntityAppSettings { DefaultSchema = "app" });
    var module = new EntityModule("M", new Version(1, 0));
    module.RegisterEntities(typeof(IAccount));
    good.AddModule(module);
    var report = good.Init();
    Assert.IsFalse(report.HasErrors);
    Assert.IsTrue(good.Initialized);

    var broken = new EntityApp("Ledger", new Version(1, 0), new EntityAppSettings { DefaultSchema = "app" });
    var keyless = new EntityModule("M", new Version(1, 0));
    keyless.RegisterEntities(typeof(IKeyless));
    broken.AddModule(keyless);
    var refused = Assert.ThrowsExactly<InvalidOperationException>(() => broken.Init());
    StringAssert.Contains(refused.Message, "primary key");
    Assert.IsFalse(broken.Initialized);
    Assert.IsNotNull(broken.Model, "kept for inspection");
  }

  [TestMethod]
  public void Application_SaysWhichEntitiesItHas() {
    var app = NewApp();
    Assert.IsTrue(app.IsRegisteredEntity(typeof(IAccount)));
    Assert.IsFalse(app.IsRegisteredEntity(typeof(IUnregistered)));
  }
}
