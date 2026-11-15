using Mic.Orm;
using Mic.Orm.Logging;

namespace Mic.Tests.Orm;

// THE END OF AN APPLICATION: what it signals, what it flushes, and that it happens once. Nothing here touches a
// database.
[TestClass]
public class EntityAppShutdownTests {
  public interface IAccount {
    [PrimaryKey, Auto] Guid Id { get; set; }
    [Size(40)] string Name { get; set; }
  }

  sealed class CollectingListener : ILogListener {
    public List<LogEntry> Entries { get; } = [];
    public void WriteBatch(IReadOnlyList<LogEntry> entries) {
      lock (Entries)
        Entries.AddRange(entries);
    }
  }

  [TestMethod]
  public async Task Shutdown_SignalsFlushesAndRunsOnce() {
    var app = new EntityApp("Ledger", new Version(1, 0), new EntityAppSettings { DefaultSchema = "app" });
    var module = new EntityModule("M", new Version(1, 0));
    module.RegisterEntities(typeof(IAccount));
    app.AddModule(module);
    app.Init();
    app.WithSqlite();
    var listener = new CollectingListener();
    app.LogService.AddListener(listener);
    var signalled = 0;
    app.Events.Shutdown += ended => { Assert.AreSame(app, ended); signalled++; };
    Assert.IsFalse(app.ShutdownToken.IsCancellationRequested);
    Assert.IsFalse(app.IsShutDown);

    // ----- the log of an operation reaches the listener once the application has shut down -----------------------------
    var context = new OperationContext(app) { LogMode = LogMode.Details };
    context.Log("the last word");
    context.PostToService();

    await using (app)
      Assert.IsFalse(app.IsShutDown, "nothing until the end of the using");
    Assert.IsTrue(app.IsShutDown);
    Assert.AreEqual(1, signalled);
    Assert.IsTrue(app.ShutdownToken.IsCancellationRequested);
    Assert.IsTrue(listener.Entries.Any(entry => entry.ToString().Contains("the last word")), "flushed");

    // ----- once ------------------------------------------------------------------------------------------------------------
    await app.ShutdownAsync();
    app.Shutdown();
    Assert.AreEqual(1, signalled, "a second shutdown does nothing");
  }
}
