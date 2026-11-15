using Mic.Orm;
using Mic.Orm.Db;
using Mic.Orm.Db.Model.Upgrades;
using Mic.Orm.Runtime;

namespace Mic.Tests.Orm.Db;

// WHERE A VENDOR'S OWN BEHAVIOUR IS TESTED. The shared suite asserts what must be true EVERYWHERE — a unique
// violation is classified as one — and cannot go further without becoming a suite that only fits PostgreSQL. This is
// the other half: the exact SQLSTATE, and the constraint name Npgsql carries as a field where Vita had to parse an
// English sentence for it.
//
// Kept deliberately small. Everything here is behaviour no other vendor has to reproduce, so anything that drifts in
// by accident is a claim quietly narrowing to one server.
[TestClass]
public sealed class PostgresSpecifics {
  static readonly PostgresFixture Fixture = new();
  TestDatabase _db = null!;

  public TestContext TestContext { get; set; } = null!;

  [TestInitialize]
  public async Task SetUpAsync() {
    if (!TestVendors.IsEnabled(Fixture.ServerKind))
      Assert.Inconclusive(TestVendors.WhyNot(Fixture.ServerKind));
    _db = await Fixture.CreateIsolatedAsync(TestContext.TestName ?? "pg");
  }

  [TestCleanup]
  public async Task TearDownAsync() {
    if (_db != null)
      await _db.DisposeAsync();
  }

  interface IParent {
    [PrimaryKey] Guid Id { get; set; }
    [Unique][Size(30)] string Code { get; set; }
  }

  async Task<Database> CreateAsync() {
    var app = new EntityApp("PgSpecifics", new Version(1, 0), new EntityAppSettings { DefaultSchema = _db.Schema });
    var module = new EntityModule("M", new Version(1, 0));
    module.RegisterEntities(typeof(IParent));
    app.AddModule(module);

    app.Init();
    Assert.IsTrue(app.Initialized, app.Report());   // mapped, never connected — this drives the pieces

    var database = new Database(app.MapTo(_db.Settings), _db.Settings);
    await database.RunDdlAsync(new DbDdlBuilder(database.Provider).CreateModel(database.DbModel), "create");
    return database;
  }

  [TestMethod]
  public async Task AUniqueViolationCarriesTheSQLSTATEAndTheCONSTRAINTName() {
    // With two unique keys on a table, only the NAME tells an application which one the user has to fix — and Npgsql
    // has it as a field, so nothing has to read the message.
    var database = await CreateAsync();
    var insert = $"INSERT INTO {_db.GetTableRef("Parent")} ({_db.Quote("Id")}, {_db.Quote("Code")}) "
      + $"VALUES ('{Guid.NewGuid()}', 'dup')";

    var ex = await Assert.ThrowsExactlyAsync<DataAccessException>(
      () => database.RunDdlAsync([insert, insert.Replace(insert[insert.IndexOf('\'')..], $"'{Guid.NewGuid()}', 'dup')")],
        "provoke"));

    Assert.AreEqual(DataAccessException.SubTypeUniqueViolation, ex.SubType);
    Assert.AreEqual("23505", ex.ProviderErrorCode);
    Assert.AreEqual("Parent", ex.TableName);
    Assert.IsNotNull(ex.KeyName);
    StringAssert.Contains(ex.KeyName, "Code");
  }

  [TestMethod]
  public async Task AMissingObjectCarriesITSState() {
    var database = await CreateAsync();
    var ex = await Assert.ThrowsExactlyAsync<DataAccessException>(
      () => database.RunDdlAsync([$"SELECT * FROM {_db.GetTableRef("NoSuchTable")}"], "provoke"));

    Assert.AreEqual(DataAccessException.SubTypeSyntaxOrMissingObject, ex.SubType);
    Assert.AreEqual("42P01", ex.ProviderErrorCode);
  }

  [TestMethod]
  public async Task ANotNullViolationNamesTheCOLUMN() {
    var database = await CreateAsync();
    var ex = await Assert.ThrowsExactlyAsync<DataAccessException>(
      () => database.RunDdlAsync([$"INSERT INTO {_db.GetTableRef("Parent")} ({_db.Quote("Id")}) VALUES ('{Guid.NewGuid()}')"],
        "provoke"));

    Assert.AreEqual(DataAccessException.SubTypeNotNullViolation, ex.SubType);
    Assert.AreEqual("Code", ex.ColumnName);
  }
}
