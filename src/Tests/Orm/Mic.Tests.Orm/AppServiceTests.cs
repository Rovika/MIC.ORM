using Mic.Orm;
using Mic.Orm.Runtime;
using Mic.Orm.Services;

namespace Mic.Tests.Orm;

// THE SERVICES AN APPLICATION SHARES, and the one claim that makes them worth having: move the application's clock and
// the DATA moves with it — the stamps and the keys alike.
//
// Nothing here touches a database.
[TestClass]
public class AppServiceTests {
  public interface IInvoice {
    [PrimaryKey, Auto(AutoType.NewGuid)] Guid Id { get; set; }
    [Size(20)] string Number { get; set; }
    [Auto(AutoType.CreatedOn)] DateTime CreatedOn { get; set; }
  }

  static EntityApp NewApp() {
    var app = new EntityApp("Billing", new Version(1, 0), new EntityAppSettings { DefaultSchema = "app" });
    var module = new EntityModule("M", new Version(1, 0));
    module.RegisterEntities(typeof(IInvoice));
    app.AddModule(module);
    app.TryInit();
    app.WithSqlite();
    Assert.IsTrue(app.Initialized, app.Report());
    return app;
  }

  [TestMethod]
  public void Application_HasDefaultsAndSaysWhenNot() {
    var app = NewApp();
    Assert.IsInstanceOfType<TimeService>(app.GetService<ITimeService>());
    Assert.IsInstanceOfType<GuidFactory>(app.GetService<IGuidFactory>());

    Assert.IsNull(app.FindService<IDisposable>());
    var ex = Assert.ThrowsExactly<InvalidOperationException>(() => app.GetService<IDisposable>());
    StringAssert.Contains(ex.Message, "IDisposable");

    // ----- and registering over one REPLACES it ---------------------------------------------------------------------
    var clock = new AdjustableTimeService(app);
    app.RegisterService<ITimeService>(clock);
    Assert.AreSame(clock, app.GetService<ITimeService>());
  }

  /// <summary>A service the application does not have is found in the host's providers: the operation's first, then
  /// the application's. The application's own services are not replaced by either.</summary>
  [TestMethod]
  public void HostServices_AreFoundAfterTheApplicationsOwn() {
    var app = NewApp();
    var rootFormat = new System.Globalization.CultureInfo("en-US");
    var scopeFormat = new System.Globalization.CultureInfo("fr-FR");
    var hostClock = new AdjustableTimeService(app);
    app.HostServices = new StubProvider { [typeof(IFormatProvider)] = rootFormat, [typeof(ITimeService)] = hostClock };

    // ----- through the application: its own first, then the host's root provider -------------------------------------
    Assert.AreSame(rootFormat, app.GetService<IFormatProvider>());
    Assert.AreNotSame(hostClock, app.GetService<ITimeService>());
    Assert.IsNull(app.FindService<IDisposable>());

    // ----- through a context without a provider: the same ------------------------------------------------------------
    using var context = new OperationContext(app);
    Assert.AreSame(rootFormat, context.GetService<IFormatProvider>());

    // ----- through a context with one: the operation's provider before the root --------------------------------------
    context.Services = new StubProvider { [typeof(IFormatProvider)] = scopeFormat, [typeof(ITimeService)] = hostClock };
    Assert.AreSame(scopeFormat, context.GetService<IFormatProvider>());
    Assert.AreNotSame(hostClock, context.GetService<ITimeService>());
    Assert.IsNull(context.FindService<IDisposable>());
    var ex = Assert.ThrowsExactly<InvalidOperationException>(() => context.GetService<IDisposable>());
    StringAssert.Contains(ex.Message, "IDisposable");

    // ----- and a child context carries the operation's provider ------------------------------------------------------
    var child = context.CreateChildContext(UserInfo.System);
    Assert.AreSame(scopeFormat, child.GetService<IFormatProvider>());
  }

  /// <summary>The point of the whole arrangement: a shifted clock reaches the DATA, by all three routes to a key.
  /// </summary>
  /// <remarks>Asserted through the KEY, because that is the half a naive implementation misses — and because it is the
  /// half that needs no database: the key is minted at `NewEntity`, from the session's instant, which comes from the
  /// application's clock. The stamps take the same instant at save; the DB suite covers that end.
  ///
  /// A v7 Guid carries a millisecond timestamp in its leading bytes, so a factory reading `DateTime.UtcNow` directly
  /// would pass every value assertion while storing the machine's time permanently in the one artefact that outlives
  /// the test.</remarks>
  [TestMethod]
  public async Task ShiftedClock_MovesTheKeys() {
    var app = NewApp();
    app.RegisterService<ITimeService>(new AdjustableTimeService(app) { TimeShift = TimeSpan.FromDays(365) });

    // ----- through a new ENTITY ------------------------------------------------------------------------------------
    var session = app.OpenSession();
    var invoice = session.NewEntity<IInvoice>();

    Assert.IsTrue(DateTimeOffset.FromUnixTimeMilliseconds(TimestampOf(invoice.Id)).UtcDateTime
      > DateTime.UtcNow.AddDays(300), "the key carries the machine's time rather than the application's");

    // ----- through the factory with NOTHING passed, which is the path that read the machine --------------------------
    //
    // `Guid.CreateVersion7()` with no argument reads the system clock. Calling it for the default case put the one bug
    // this arrangement exists to prevent inside the default implementation of it — every stamp would follow a shifted
    // clock and every KEY would quietly not. It also fixes the ORDER problem: the clock is asked for per call, so a
    // service registered AFTER the factory was constructed — which is every replacement, since the defaults are made in
    // the app's constructor — is the one used.
    var minted = app.GetService<IGuidFactory>().NewGuid(GuidScheme.V7);
    Assert.IsTrue(DateTimeOffset.FromUnixTimeMilliseconds(TimestampOf(minted)).UtcDateTime
      > DateTime.UtcNow.AddDays(300), "the key was minted from the machine's clock");

    // ----- and through the factory with an EXPLICIT instant ------------------------------------------------------------
    var explicitly = new DateTime(2035, 6, 1, 12, 0, 0, DateTimeKind.Utc);
    Assert.AreEqual(new DateTimeOffset(explicitly).ToUnixTimeMilliseconds(),
      TimestampOf(app.GetService<IGuidFactory>().NewGuid(GuidScheme.V7, explicitly)));
  }

  [TestMethod]
  public void GuidFactory_MintsEachScheme() {
    var factory = NewApp().GetService<IGuidFactory>();
    var instant = new DateTime(2035, 6, 1, 12, 0, 0, DateTimeKind.Utc);
    var milliseconds = new DateTimeOffset(instant).ToUnixTimeMilliseconds();

    var v7 = factory.NewGuid(GuidScheme.V7, instant);
    Assert.AreEqual(7, v7.Version);
    Assert.AreEqual(milliseconds, TimestampOf(v7));

    // The SQL Server layout: the same millisecond in the last six bytes, the ones that server compares first.
    var v8 = factory.NewGuid(GuidScheme.V8SqlServer, instant);
    Assert.AreEqual(8, v8.Version);
    Assert.AreEqual(milliseconds, v8.ToByteArray(bigEndian: true)[10..].Aggregate(0L, (ms, b) => (ms << 8) | b));

    Assert.AreEqual(4, factory.NewGuid(GuidScheme.V4, instant).Version);
    Assert.ThrowsExactly<ArgumentException>(() => factory.NewGuid(GuidScheme.Auto));
  }

  // Guids made at one instant, each given its place: they sort in that order, as the server each layout is for
  // compares them, and the time moves a millisecond ahead where the twelve bits of the place run out.
  [TestMethod]
  public void GuidFactory_OrdersTheGuidsOfOneInstant() {
    var factory = NewApp().GetService<IGuidFactory>();
    var instant = new DateTime(2035, 6, 1, 12, 0, 0, DateTimeKind.Utc);
    const int count = 10_000;

    var v7 = Enumerable.Range(0, count).Select(i => factory.NewGuid(GuidScheme.V7, instant, i)).ToList();
    CollectionAssert.AreEqual(v7, v7.OrderBy(g => g.ToString("N"), StringComparer.Ordinal).ToList(),
      "byte by byte, first to last");
    Assert.IsTrue(v7.All(g => g.Version == 7 && (g.Variant & 0xC) == 0x8));
    Assert.AreEqual(count, v7.Distinct().Count());

    // SQL Server's order over a uniqueidentifier, which SqlGuid implements.
    var v8 = Enumerable.Range(0, count).Select(i => factory.NewGuid(GuidScheme.V8SqlServer, instant, i)).ToList();
    CollectionAssert.AreEqual(v8, v8.OrderBy(g => new System.Data.SqlTypes.SqlGuid(g)).ToList());
    Assert.IsTrue(v8.All(g => g.Version == 8 && (g.Variant & 0xC) == 0x8));
    Assert.AreEqual(count, v8.Distinct().Count());

    // ----- the time: exact for the first 4,096, a millisecond ahead for every 4,096 after ---------------------------
    foreach (var guids in new[] { v7, v8 }) {
      Assert.AreEqual(instant, EntityHelper.GetTimestamp(guids[0]));
      Assert.AreEqual(instant, EntityHelper.GetTimestamp(guids[4095]));
      Assert.AreEqual(instant.AddMilliseconds(1), EntityHelper.GetTimestamp(guids[4096]));
      Assert.AreEqual(instant.AddMilliseconds(2), EntityHelper.GetTimestamp(guids[9999]));
    }

    // ----- with no place given the bits after the time are random, as before ---------------------------------------
    var unplaced = Enumerable.Range(0, 200).Select(_ => factory.NewGuid(GuidScheme.V7, instant)).ToList();
    CollectionAssert.AreNotEqual(unplaced, unplaced.OrderBy(g => g.ToString("N"), StringComparer.Ordinal).ToList());
  }

  /// <summary>Every entity created before one save shares ONE instant — key and stamp alike.</summary>
  /// <remarks>The two are minted at different moments: the Guid at `NewEntity`, because a client key must exist before
  /// anything can reference the entity, and the stamps at `SaveChanges`. Without a held instant they would disagree by
  /// however long the session was open — and a v7 key stores that disagreement permanently.</remarks>
  [TestMethod]
  public async Task OneSave_MintsOneInstant() {
    var app = NewApp();
    var session = app.OpenSession();

    var first = session.NewEntity<IInvoice>();
    await Task.Delay(30);
    var second = session.NewEntity<IInvoice>();

    Assert.AreEqual(TimestampOf(first.Id), TimestampOf(second.Id),
      "two entities of one save were keyed from different instants");
    Assert.AreNotEqual(first.Id, second.Id, "…but they are still different keys");
  }

  /// <summary>The first six bytes of a v7 Guid, big-endian, ARE the Unix milliseconds it was minted at.</summary>
  static long TimestampOf(Guid key) {
    var bytes = key.ToByteArray(bigEndian: true);
    var milliseconds = 0L;
    for (var i = 0; i < 6; i++)
      milliseconds = (milliseconds << 8) | bytes[i];
    return milliseconds;
  }

  /// <summary>Represents a host's service provider over a dictionary.</summary>
  sealed class StubProvider : Dictionary<Type, object>, IServiceProvider {
    public object? GetService(Type serviceType) => TryGetValue(serviceType, out var service) ? service : null;
  }
}
