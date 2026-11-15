using Mic.Orm;
using Mic.Orm.Runtime;

namespace Mic.Tests.Orm;

// THE CONTEXT OF ONE OPERATION: what it carries, what it hands to a child, what it raises and throws, and what it
// ends. Nothing here touches a database.
[TestClass]
public class OperationContextTests {
  public interface IAccount {
    [PrimaryKey, Auto] Guid Id { get; set; }
    [Size(40)] string Name { get; set; }
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
  public void Context_HasItsDefaultsAndHandsThemToAChild() {
    var app = NewApp();
    using var context = new OperationContext(app, new UserInfo(Guid.NewGuid(), "ada")) {
      UserCulture = "fr-FR", UserSession = new UserSessionBase { SessionId = Guid.NewGuid() }, DatabaseAlias = "(default)",
    };
    var host = new object();   // what ASP.NET's HttpContext is to the ORM: the host's, carried and handed back
    context.HttpContext = host;
    context.CorrelationId = "corr-1";
    Assert.AreEqual(DbConnectionReuseMode.NoReuse, context.ConnectionReuseMode, "every host starts safe; KeepOpen is opted into");
    Assert.AreEqual("corr-1", context.CorrelationId);
    context.SetValue("TenantId", 42);

    var child = context.CreateChildContext(UserInfo.System);
    Assert.AreSame(UserInfo.System, child.User);
    Assert.AreSame(host, child.HttpContext);
    Assert.IsNull(new OperationContext(app).HttpContext, "a job, a test, a console: no host context");
    Assert.AreSame(context.UserSession, child.UserSession);
    Assert.AreEqual("fr-FR", child.UserCulture);
    Assert.AreEqual("(default)", child.DatabaseAlias);
    Assert.AreEqual("corr-1", child.CorrelationId);
    Assert.AreEqual(context.ConnectionReuseMode, child.ConnectionReuseMode);
    Assert.IsFalse(child.TryGetValue("TenantId", out _), "the values are the request's, not carried to the system's work");

    // ----- the users ---------------------------------------------------------------------------------------------------
    var byNumber = new UserInfo(17, "bob");
    Assert.AreEqual(UserInfo.BlankUserId, byNumber.UserId);
    Assert.AreEqual(17, byNumber.AltUserId);
    Assert.AreEqual(UserKind.AuthenticatedUser, byNumber.Kind);
    Assert.AreEqual(UserInfo.GetKey(UserInfo.BlankUserId, 17), byNumber.Key);
    Assert.AreSame(UserInfo.System, UserInfo.Create(UserKind.System, null, null, null));
    Assert.AreSame(UserInfo.Anonymous, UserInfo.Create(UserKind.Anonymous, null, null, null));
    Assert.AreEqual(UserInfo.BlankAltUserId, UserInfo.Create(UserKind.AuthenticatedUser, Guid.NewGuid(), null, "eve").AltUserId);
    Assert.ThrowsExactly<ArgumentException>(() => UserInfo.Create(UserKind.AuthenticatedUser, null, null, "eve"));
    Assert.IsFalse(UserInfo.Anonymous.IsAuthenticated);
    Assert.IsTrue(UserInfo.System.IsAuthenticated);
  }

  [TestMethod]
  public void Context_CarriesIncludesForItsSessions() {
    var app = NewApp();
    using var context = new OperationContext(app);
    Assert.IsFalse(context.HasIncludes());
    Assert.AreSame(context, context.AddInclude<IAccount>(a => a.Name));
    Assert.IsTrue(context.HasIncludes());
    Assert.AreEqual(1, context.GetIncludes().Count);

    // Removed by the same expression, or by one that reads the same.
    Assert.IsFalse(context.RemoveInclude<IAccount>(a => a.Id));
    Assert.IsTrue(context.RemoveInclude<IAccount>(a => a.Name));
    Assert.IsFalse(context.HasIncludes());
  }

  [TestMethod]
  public void Context_CollectsClientFaultsAndThrowsThemOnce() {
    var app = NewApp();
    using var context = new OperationContext(app);
    Assert.IsFalse(context.HasClientFaults());
    context.ThrowValidation();   // nothing to throw

    context.AddClientFault(new ClientFault(ClientFaultCodes.ObjectNotFound, "No such account.") { EntityRef = "Account/7" });
    context.AddClientFault(new ClientFault(ClientFaultCodes.InvalidValue, "Too small.", "Amount"));
    Assert.IsTrue(context.HasClientFaults());
    Assert.AreEqual(2, context.ClientFaults.Count);
    Assert.AreEqual("Account/7", context.GetClientFaults()[0].EntityRef);
    Assert.AreEqual("Amount: Too small.", context.GetClientFaults()[1].ToString());

    var ex = Assert.ThrowsExactly<ClientFaultException>(context.ThrowValidation);
    CollectionAssert.AreEqual(new[] { "No such account.", "Too small." }, ex.GetMessages());
    StringAssert.Contains(ex.Message, "2 faults");
    Assert.IsFalse(context.HasClientFaults(), "thrown is delivered: a second ThrowValidation has nothing");
    context.ThrowValidation();

    // ----- the session's own validation throws the same exception, with the record on the fault --------------------
    var session = context.OpenSession();
    var account = session.NewEntity<IAccount>();
    account.Name = new string('x', 41);
    var refused = Assert.ThrowsExactly<ClientFaultException>(session.ValidateChanges);
    Assert.AreEqual(ClientFaultCodes.ValueTooLong, refused.Faults[0].Code);
    Assert.AreEqual("Name", refused.Faults[0].MemberName);
    Assert.IsNotNull(refused.Faults[0].Record);
    StringAssert.StartsWith(refused.Faults[0].ToString(), "Account.Name:");
  }

  [TestMethod]
  public async Task Context_EndsWhatRegisteredWithIt() {
    var app = NewApp();
    var context = new OperationContext(app);
    var ended = new List<string>();
    context.RegisterDisposable(new Ender(() => ended.Add("sync")));
    context.RegisterDisposable(new AsyncEnder(() => ended.Add("async")));
    var session = (EntitySession)context.OpenSession();
    var stub = session.GetEntity<IAccount>(Guid.NewGuid(), LoadFlags.Stub);
    Assert.IsNotNull(stub);

    // A registration holds nothing alive: the registry is swept past the threshold, under the lock.
    for (var i = 0; i < 60; i++)
      context.RegisterDisposable(new Ender(() => { }));

    context.LogMode = LogMode.Details;
    context.Log("one");
    context.Log("two");
    StringAssert.Contains(context.GetLogContents(), "one");
    StringAssert.Contains(context.GetLogContents(), "two");

    await context.DisposeAsync();
    CollectionAssert.AreEqual(new[] { "sync", "async" }, ended, "in the order registered");
    Assert.AreEqual("", context.GetLogContents(), "posted at the end");
    Assert.IsNull(session.Connection);

    // ----- cancellation: the context's own signal, or a token from the host -------------------------------------------
    var cancelled = new OperationContext(app);
    cancelled.ThrowIfCancelled();
    cancelled.SignalCancellation();
    Assert.IsTrue(cancelled.CancellationToken.IsCancellationRequested);
    Assert.ThrowsExactly<OperationCanceledException>(cancelled.ThrowIfCancelled);
    using var source = new CancellationTokenSource();
    var external = new OperationContext(app);
    external.SetExternalCancellationToken(source.Token);
    source.Cancel();
    Assert.IsTrue(external.CancellationToken.IsCancellationRequested);
  }

  sealed class Ender(Action onDispose) : IDisposable {
    public void Dispose() => onDispose();
  }

  sealed class AsyncEnder(Action onDispose) : IAsyncDisposable {
    public ValueTask DisposeAsync() {
      onDispose();
      return ValueTask.CompletedTask;
    }
  }
}
