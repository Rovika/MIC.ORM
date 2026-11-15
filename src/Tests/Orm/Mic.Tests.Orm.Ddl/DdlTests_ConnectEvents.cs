using Mic.Orm;
using Mic.Orm.Db;
using Mic.Orm.Db.Model.Upgrades;

namespace Mic.Tests.Orm.Db;

// WHAT AN APPLICATION IS TOLD WHEN IT REACHES A DATABASE. Here rather than with the other event tests because of what
// they need: a database MIC has never seen, so that connecting is a first connect and the upgrade has something to
// apply. The data suite shares one already-connected database, where both of those are false.
public abstract partial class DdlTests {
  static class ConnectEvents {
    public interface ITicket {
      [PrimaryKey, Auto(AutoType.Identity)] long Id { get; set; }
      [Size(50)] string Title { get; set; }
    }
  }

  [TestMethod]
  public async Task Connecting_AnnouncesUpgradeThenItself() {
    var app = AppFor("EventsConnect", new Version(1, 0), typeof(ConnectEvents.ITicket));
    var order = new List<string>();
    DbUpgradeReport? seenReport = null;
    Database? seenSource = null;
    app.DatabaseEvents.Upgrading += _ => order.Add("upgrading");
    app.DatabaseEvents.Upgraded += (_, report) => { order.Add("upgraded"); seenReport = report; };
    app.DatabaseEvents.Connected += source => { order.Add("connected"); seenSource = source; };

    await app.ConnectToAsync(Db.Settings);
    var report = app.UpgradeFor(Db.Settings.DatabaseAlias);

    CollectionAssert.AreEqual(new[] { "upgrading", "upgraded", "connected" }, order,
      "got: " + string.Join(", ", order));
    Assert.AreSame(report, seenReport, "the handler gets the very report the application keeps");
    Assert.IsNotNull(seenSource);
    Assert.AreEqual(Db.Settings.DatabaseAlias, seenSource!.Alias);
  }

  [TestMethod]
  public async Task Connected_MeansTheSchemaIsSettled() {
    // `Connected` is meant to mean USABLE, so it comes after the upgrade — a database that answers but whose schema
    // does not match the model is not something to start serving from.
    var app = AppFor("EventsUsable", new Version(1, 0), typeof(ConnectEvents.ITicket));
    var appliedWhenConnected = (bool?)null;
    app.DatabaseEvents.Connected += _ =>
      appliedWhenConnected = app.UpgradeFor(Db.Settings.DatabaseAlias)?.IsApplied;

    await app.ConnectToAsync(Db.Settings);

    Assert.IsTrue(appliedWhenConnected, "the tables were created before anyone was told the source was connected");
  }
}
