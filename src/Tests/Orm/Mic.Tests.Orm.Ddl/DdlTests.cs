namespace Mic.Tests.Orm.Db;

// THE SCHEMA HALF. Everything about getting a database into the shape the model describes, and reading back what is
// there: DDL generation, introspection, the upgrade loop, foreign metadata, and the coordination that keeps two
// instances from upgrading at once.
//
// APART FROM THE DATA HALF because of what it costs to run. These tests are ABOUT schema churn — most of them need a
// database in a known state and then change it — so they keep a schema per test. Work on querying and saving should
// not have to pay for what they do, which is what `Mic.Tests.Orm.Dml` is for.
//
// THE PER-TEST SCHEMA IS NOT THE EXPENSIVE PART, though it reads as if it should be and this comment used to say so.
// Measured on Oracle, the slowest vendor: CREATE USER 13ms, DROP USER CASCADE 23ms — forty milliseconds a test, half
// a second across the suite. What costs is what the tests DO in those schemas: four of them build every shape MIC can
// express and read the catalog back, at 16 to 25 seconds each on Oracle. See `TESTING.md`.
//
// The tests live in DdlTests.*.cs by subject and the vendor classes are in Vendors.cs; the base class, the fixtures
// and the per-test database come from Mic.Tests.Orm.DbHarness.
public abstract partial class DdlTests : DbTestsBase {
  /// <summary>A database of this test's OWN, made before it runs and taken away after.</summary>
  /// <remarks>Kept here deliberately, and CHEAPLY — see the class remarks for the measurement. These tests create
  /// schemas, alter them, compare them against a model and upgrade them, and several assert on what a database looks
  /// like when MIC has never seen it, so a shared one would be a shared thing being changed underneath.
  ///
  /// `DbInfo` is the reason that is not merely inconvenient: there is ONE ROW PER DATABASE, because there is one
  /// entity model per database. Two tests sharing a schema would share one record of its version, and the second
  /// would find the first's — so its connect would not be a first connect, which is exactly what several of these
  /// assert on.
  ///
  /// LAZY: `CreateIsolatedAsync` registers the schema and drops any leftover; the schema itself is created when a
  /// test first needs one. A test that never builds one never pays for one, which is why two of these finish in under
  /// a second.</remarks>
  [TestInitialize]
  public async Task CreateOwnDatabaseAsync() =>
    Db = await Fixture.CreateIsolatedAsync(TestContext.TestName ?? "test");

  [TestCleanup]
  public async Task DropOwnDatabaseAsync() {
    if (Db != null)
      await Db.DisposeAsync();
  }
}
