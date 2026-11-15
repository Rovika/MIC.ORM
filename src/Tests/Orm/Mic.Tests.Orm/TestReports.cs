using Mic.Orm;
using Mic.Orm.Db.Model.Upgrades;
using Mic.Orm.Logging;

namespace Mic.Tests.Orm;

// WHAT A TEST SAYS WHEN AN APPLICATION WILL NOT START.
//
// It used to be the application's activation report, one property holding everything startup did. That report is gone —
// its three accounts now live on the things they are about — and the idiom appeared in about a hundred assertions, so
// composing the replacement belongs in ONE place rather than a hundred.
//
// IT IS ALSO NOT A STRAIGHT SUBSTITUTION, which is the real argument for a helper. A model that failed to build says
// so on its own report; a hook that THREW says so in the LOG, and nothing on the model knows about it. A message
// assembled from the report alone would be silent about the second, so this collects both.
//
// HERE RATHER THAN IN THE HARNESS because `Mic.Tests.Orm` does not reference `Mic.Tests.Orm.DbHarness` — deliberately,
// since ORM_31 kept the model-only suite container-free and fast — and one helper does not justify a project to share
// it. The four sites in the db suites use `app.Model!.BuildReport.Format()` directly.
public static class TestReports {
  /// <summary>Builds the model and says whether it built, for the tests that build models meant to be refused.
  /// </summary>
  public static bool TryInit(this EntityApp app) {
    try {
      app.Init();
    } catch (InvalidOperationException) {
      // The report is on the application; the test reads it there.
    }
    return app.Initialized;
  }


  /// <summary>Why this application is not usable — the model's own account, plus anything startup logged.</summary>
  public static string Report(this EntityApp app) {
    var lines = new List<string>();
    lines.Add(app.Model == null ? "Init() has not been run." : app.Model.BuildReport.Format());
    lines.AddRange(StartupErrors(app).Select(e => e.ToString().TrimEnd()));
    foreach (var source in app.DataAccess.Databases)
      if (source.UpgradeReport is { } upgrade)
        lines.Add($"Database '{source.Alias}': {upgrade.Report()}");
    return string.Join(Environment.NewLine, lines);
  }

  /// <summary>Everything a BUILD found — the entity model's, and every db model's.</summary>
  /// <remarks>THE UNION, where the reports themselves are deliberately separate. A test asking "was this reported?"
  /// does not care which pass found it, and the old activation report held both — so the split belongs in the
  /// production types, not in every assertion.
  ///
  /// Three sources, because a mapping that FAILED is not registered and its report would otherwise be unreachable:
  /// the entity model, each registered database's db model, and what startup logged. Empty rather than null when
  /// `Init` never ran, so an assertion reads the same either way.</remarks>
  public static IReadOnlyList<MessageLogEntry> Diagnostics(this EntityApp app) => [
    .. app.Model?.BuildReport.Messages ?? [],
    .. app.DataAccess.Databases.SelectMany(s => s.DbModel.BuildReport.Messages),
    .. StartupErrors(app).OfType<MessageLogEntry>(),
  ];

  /// <summary>What happened at one database, or null if that database was never connected.</summary>
  public static DbUpgradeReport? UpgradeFor(this EntityApp app, string? databaseAlias = null) =>
    app.DataAccess.FindDatabase(databaseAlias)?.UpgradeReport;

  // ----- what startup LOGGED ------------------------------------------------------------------------------------------
  //
  // A fault in application code — a module's `OnModelConstructed` throwing — is a thing that HAPPENED rather than a
  // property of the model, so it goes to the log. A test that wants to see it therefore has to listen, which is what
  // this does: attach on first ask, and keep what arrives.

  static readonly Dictionary<EntityApp, Collector> _collectors = [];

  /// <summary>Start collecting this application's log BEFORE `Init` runs, so a startup fault is caught.</summary>
  /// <remarks>Called by a test that intends to inspect one. Not automatic: attaching a listener to every application a
  /// test builds would make the log service part of every fixture for the sake of a handful of assertions.</remarks>
  public static EntityApp CollectingLog(this EntityApp app) {
    var collector = new Collector();
    _collectors[app] = collector;
    app.LogService.AddListener(collector);
    return app;
  }

  /// <summary>What this application logged during startup — empty unless <see cref="CollectingLog"/> was called.
  /// </summary>
  public static IReadOnlyList<LogEntry> StartupErrors(this EntityApp app) {
    if (!_collectors.TryGetValue(app, out var collector))
      return [];
    app.LogService.FlushAsync().GetAwaiter().GetResult();
    return collector.Entries;
  }

  sealed class Collector : ILogListener {
    public List<LogEntry> Entries { get; } = [];
    public void WriteBatch(IReadOnlyList<LogEntry> batch) => Entries.AddRange(batch);
  }
}
