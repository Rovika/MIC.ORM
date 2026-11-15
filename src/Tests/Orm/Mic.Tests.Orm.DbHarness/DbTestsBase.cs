using Mic.Orm;
using Mic.Orm.Db;
using Mic.Orm.Db.Model.Upgrades;
using Mic.Orm.Db.Provider;
using Mic.Orm.Logging;
using Mic.Orm.Runtime;

namespace Mic.Tests.Orm.Db;

// WHAT BOTH DB SUITES SHARE: a fixture per vendor, the vendor check, and the helpers that turn entity types into a
// model against whatever database the suite has given itself.
//
// WHAT IT DOES NOT DECIDE IS WHERE THAT DATABASE COMES FROM, and that is the whole reason the two suites are apart.
// The DDL suite is ABOUT schema churn, so it takes a database of its OWN per test — see `DdlTests`. The DML suite is
// merely delayed by schema churn, so it shares ONE per vendor — see `DmlTests`. Putting the per-test creation here, as
// this class used to, made every data test pay six seconds of `CREATE USER` on Oracle to insert one row.
//
// CLASS-LEVEL PARALLELISM (see each suite's AssemblyInfo) makes one class per vendor mean VENDORS IN PARALLEL, TESTS
// WITHIN A VENDOR SEQUENTIAL. Wall clock lands near the slowest vendor rather than the sum of all of them — and it is
// also what makes a shared database safe, since tests in one vendor never race each other.
public abstract class DbTestsBase {
  protected abstract ProviderFixture Fixture { get; }

  /// <summary>The database this test works in — its own, or the one its suite shares.</summary>
  protected TestDatabase Db { get; set; } = null!;

  public TestContext TestContext { get; set; } = null!;

  /// <summary>The vendor check, and nothing else. Each suite adds its own database in its own initialiser.</summary>
  /// <remarks>MSTest runs a base class's initialiser BEFORE the derived one, which is the ordering this needs: a
  /// vendor that is not in the run must be skipped before anything tries to reach it.</remarks>
  [TestInitialize]
  public void CheckVendor() {
    // A vendor not in this run is SKIPPED, not failed — the difference between "untested" and "broken", and what lets
    // the ordinary inner loop stay fast without quietly testing nothing.
    if (!TestVendors.IsEnabled(Fixture.ServerKind))
      Assert.Inconclusive(TestVendors.WhyNot(Fixture.ServerKind));

    // AFTER the vendor check, so a skipped vendor writes no header for a test it never ran.
    if (LoggingEnabled)
      TestLog.WriteLine($"{Environment.NewLine}===== {TestContext.TestName} "
        + "===================================================================");
  }

  // ----- the SQL log ----------------------------------------------------------------------------------------------
  //
  // A FOLDER PER PROVIDER AND A FILE PER TEST CLASS: `bin/logs/{Provider}/{Class}.log`. Two axes because the run has
  // two: vendors go in parallel, so they cannot share a file; and a class is the unit somebody actually opens — the
  // whole of `DmlTests` against PostgreSQL is one readable artefact, where everything against everything is not.
  //
  // Tests are delimited INSIDE their class's file rather than given a file each — separators only earn their place if
  // tests share one, and with a file per test you would find it by name and never read a separator.
  //
  // `FileLogListener` locks per PATH, so all of these are written concurrently and no one file is ever interleaved.

  static readonly System.Collections.Concurrent.ConcurrentDictionary<string, FileLogListener> _logs = new();

  /// <summary>Whether the suites write their SQL log at all.</summary>
  /// <remarks>A SWITCH BECAUSE IT COSTS SOMETHING. Recording every statement with its parameters and writing megabytes
  /// of text is not free, and the test refactor before this task existed to keep the inner loop fast. Set
  /// `MIC_TEST_LOG=0` to turn it off; anything else, including unset, leaves it on.</remarks>
  static readonly bool LoggingEnabled =
    Environment.GetEnvironmentVariable("MIC_TEST_LOG") is not "0" and not "false";

  /// <summary>This class's log file, under this provider's folder. Truncated once per run, when first asked for.
  /// </summary>
  protected FileLogListener TestLog => _logs.GetOrAdd(LogPath, path => {
    // A run starts a fresh file: a log that accumulated across runs would have yesterday's failure in it, which is
    // worse than no log at all. Keyed by path, so this happens once per FILE however many tests the class has.
    if (File.Exists(path))
      File.Delete(path);
    return new FileLogListener(path);
  });

  string LogPath {
    get {
      var className = TestContext.FullyQualifiedTestClassName?.Split('.')[^1] ?? "Tests";
      // The vendor is the FOLDER, so a class named for it too would read `Postgres/Postgres_DmlTests.log`. Stripped
      // where the convention put it there — `Postgres_DmlTests` becomes `DmlTests` — and left alone otherwise.
      var prefix = Fixture.ServerKind + "_";
      if (className.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        className = className[prefix.Length..];
      return Path.Combine(AppContext.BaseDirectory, "logs", Fixture.ServerKind.ToString(), $"{className}.log");
    }
  }

  /// <summary>Mark a SECTION within a test — what a multi-step method uses to say which step the next statements are.
  /// </summary>
  /// <remarks>Straight to the file rather than through the log, because it belongs to no session and no operation: it
  /// is the test talking about itself. `session.LogMessage(…)` is the other half, for a message that should group with
  /// a particular session's statements.
  ///
  /// Vita's equivalent is `WriteTestTitle`, used the same way — its Linq tests mark every query they are about to run.
  /// The per-TEST banner needs no such call here: `CheckVendor` writes it automatically, which is one fewer thing to
  /// remember and one fewer test that quietly has no header.</remarks>
  protected void WriteTestTitle(string title) {
    if (LoggingEnabled)
      TestLog.WriteLine($"{Environment.NewLine}----- {title} -----");
  }

  readonly List<OperationContext> _contexts = [];
  readonly HashSet<EntityApp> _logged = [];

  // The log files each application already writes to. Static, because a test class instance lives for one test and
  // an application may serve every test of the class; weak, so an application made for one test goes with it.
  static readonly System.Runtime.CompilerServices.ConditionalWeakTable<EntityApp, HashSet<string>> _listening = new();

  /// <summary>A context for this test — RECORDED, so its log reaches the file when the test ends.</summary>
  /// <remarks>THE REASON TESTS GO THROUGH THIS. A session does not dispose its context, and correctly so — a context
  /// outlives its sessions, which is the whole reason the log lives on it. So nothing would post at the end of a test
  /// unless something owned the contexts, and this is that something. It is also a truer model of how a web request
  /// uses one: several sessions, one context, one combined log.</remarks>
  protected OperationContext NewContext(EntityApp app, UserInfo? user = null) {
    EnableLogging(app);
    var context = new OperationContext(app, user);
    _contexts.Add(context);
    return context;
  }

  /// <summary>Turn logging on for an application, once. Before any context is made from it — the mode is read at
  /// construction.</summary>
  void EnableLogging(EntityApp app) {
    if (!LoggingEnabled || !_logged.Add(app))
      return;
    app.Settings.LogMode = LogMode.Details;

    // The listener is added once for an application, not once a test: added again, it writes every batch again,
    // and a shared application would write its hundredth test a hundred times.
    var files = _listening.GetOrCreateValue(app);
    lock (files)
      if (!files.Add(LogPath))
        return;
    app.LogService.AddListener(TestLog);
  }

  /// <summary>Post what the test's contexts recorded, and WAIT for it to reach the file.</summary>
  /// <remarks>Async because the waiting is the point: writing happens on a background thread, and a cleanup that did
  /// not wait would let the next test's header overtake this test's statements.</remarks>
  [TestCleanup]
  public async Task FlushTestLog() {
    foreach (var context in _contexts)
      await context.DisposeAsync();
    foreach (var app in _logged)
      await app.LogService.FlushAsync();
    _contexts.Clear();
    _logged.Clear();
  }

  // ----- what a test needs to build a model -----------------------------------------------------------------------

  /// <summary>An application over these entities, in this test's own database. <paramref name="configure"/> runs
  /// BEFORE the build, which is the only moment some settings can still be changed.</summary>
  protected EntityApp BuildApp(string appName, Version version, Action<EntityApp>? configure,
      params Type[] entities) {
    var app = new EntityApp(appName, version, new EntityAppSettings { DefaultSchema = Db.Schema });
    var module = new EntityModule("M", new Version(1, 0));
    module.RegisterEntities(entities);
    app.AddModule(module);

    configure?.Invoke(app);
    app.Init();
    Assert.IsTrue(app.Initialized, app.Model!.BuildReport.Format());

    // HERE RATHER THAN ONLY IN `NewContext`, because the tests that matter most for a log never make one: a test that
    // calls `ConnectToAsync` has its context created INSIDE the ORM, and an application not switched on by then logs
    // nothing at all. That is precisely the connect-and-upgrade path.
    EnableLogging(app);

    // MAPPED HERE, not at build: the provider comes with the settings, and a database is what owns the db model.
    // No connectivity check — these applications are built far more often than they are talked to.
    AppInitHelper.CreateDatabase(app, Db.Settings);
    return app;
  }

  protected EntityApp AppFor(string appName, Version version, params Type[] entities) =>
    BuildApp(appName, version, null, entities);

  protected EntityApp AppFor(params Type[] entities) => AppFor("TestApp", new Version(1, 0), entities);

  /// <summary>The db model and settings, without the app — for the tests that drive the pieces directly.</summary>
  protected Database DatabaseFor(params Type[] entities) =>
    new(AppFor(entities).MappedModel(), Db.Settings);

  /// <summary>Build the model, generate its DDL, run it. What most tests need before they can test anything.</summary>
  protected async Task<Database> CreateSchemaAsync(params Type[] entities) {
    var app = AppFor(entities);
    var database = new Database(app.MappedModel(), Db.Settings);
    // THROUGH A CONTEXT, so the DDL reaches the log. A schema creation has no session and would otherwise be invisible
    // — which is what left the DDL suite's log a list of test headers with nothing under any of them.
    await new DdlExecutor(database).ExecuteAsync(
      new DbDdlBuilder(database.Provider).CreateModel(database.DbModel), NewContext(app), "create");
    return database;
  }

  // ----- saying why a test does not apply here ---------------------------------------------------------------------

  /// <summary>Skip when this server cannot do what the test is about.</summary>
  /// <remarks>By CAPABILITY, never by server kind. `Supports(AlterColumn)` keeps vendor knowledge in the provider
  /// where the rest of it lives, and a test written that way stays right when a fifth vendor arrives — where
  /// `if (ServerKind == SQLite)` would quietly be wrong.
  ///
  /// Every use is a hole in the claim that behaviour is identical above the driver, so the COUNT of these is a metric
  /// to watch rather than a mechanism to reach for.</remarks>
  protected void RequiresFeature(DbFeatures feature) {
    if (!Db.Provider.Supports(feature))
      Assert.Inconclusive($"{Db.Provider.ServerKind} does not support {feature}.");
  }
}
