using Mic.Orm;
using Mic.Orm.Db;
using Mic.Orm.Db.Model;
using Mic.Orm.Db.Model.Upgrades;
using Mic.Orm.Db.Provider;
using Mic.Orm.Runtime;

namespace Mic.Tests.Orm.Db;

// WHAT EVERY PHASE BEFORE PHASE 3 WAS BUILDING TOWARD: an entity model, mapped, turned into DDL, run against a real
// server. Nothing here names a vendor.
public abstract partial class DdlTests {
  static class Ddl {
    [Entity(Name = "Person")]
    public interface IPersonV1 {
      [PrimaryKey] Guid Id { get; set; }
      [Size(50)] string LastName { get; set; }
    }

    [Entity(Name = "Person")]
    public interface IPersonV2 {
      [PrimaryKey] Guid Id { get; set; }
      [Size(80)] string LastName { get; set; }
      [Column(Default = "0")] int Age { get; set; }
    }

    [Entity(Name = "Person")]
    public interface IPersonRequired {
      [PrimaryKey] Guid Id { get; set; }
      [Size(50)] string LastName { get; set; }
      [Size(30)] string Nickname { get; set; }        // required, no default
    }

    public interface IAddress {
      [PrimaryKey] Guid Id { get; set; }
      [Size(100)] string Street { get; set; }
    }
  }

  /// <summary>One person, so an upgrade meets a table that already has rows — the only case where half of this
  /// matters.</summary>
  async Task InsertOnePersonAsync() =>
    await Db.ExecuteAsync($"INSERT INTO {Db.GetTableRef("Person")} ({Db.Quote("Id")}, {Db.Quote("LastName")}) "
      + $"VALUES ({Db.GuidLiteral(Guid.NewGuid())}, 'Larsen')");

  async Task<DbColumnInfo> ColumnAsync(string table, string column) {
    var loaded = await Db.LoadAsync();
    var loadedTable = loaded.FindTable(Db.Schema, table);
    Assert.IsNotNull(loadedTable, $"table '{table}' is not there");
    var col = loadedTable.FindColumn(column);
    Assert.IsNotNull(col, $"column '{table}.{column}' is not there");
    return col;
  }

  // ----- the tests --------------------------------------------------------------------------------------------------

  [TestMethod]
  public async Task ARequiredColumnNeedsNOMigrationToSucceed() {
    // The case that used to fail: a model gains a required column, the table already has rows, and nobody wrote a
    // migration. The server rejected the tightening, so the ORDINARY model change was the one that broke.
    var before = await CreateSchemaAsync(typeof(Ddl.IPersonV1));
    await InsertOnePersonAsync();

    var after = DatabaseFor(typeof(Ddl.IPersonRequired));
    var changes = after.Provider.CreateDbModelComparer().Compare(before.DbModel, after.DbModel);
    var script = new DbUpgradeScriptBuilder(after.Provider).Build(changes);      // no migration
    Assert.AreEqual(0, script.Warnings.Count, string.Join("; ", script.Warnings));

    await after.RunDdlAsync(script);

    Assert.IsFalse((await ColumnAsync("Person", "Nickname")).IsNullable);

    // THE PROVIDER'S OWN placeholder, not the empty string. Oracle treats `''` AS NULL, so filling a column with it
    // and then tightening the column would fail on the value that was supposed to make tightening possible — its
    // placeholder is a single space. Asserting the literal here would be asserting one vendor's answer.
    var placeholder = after.Provider.TypeRegistry.GetDefaultColumnInit(typeof(string))!.Trim('\'');
    Assert.AreEqual(placeholder,
      await Db.ScalarAsync($"SELECT {Db.Quote("Nickname")} FROM {Db.GetTableRef("Person")}"),
      "the existing row should have been backfilled with the placeholder");
  }

  [TestMethod]
  public async Task AMigrationStillGetsFirstRefusalOnEveryRow() {
    // The placeholder is a safety net, not something that overwrites real values: it runs AFTER the AddColumns
    // migration slot and fills only what is still null.
    var before = await CreateSchemaAsync(typeof(Ddl.IPersonV1));
    await InsertOnePersonAsync();

    var after = DatabaseFor(typeof(Ddl.IPersonRequired));
    var changes = after.Provider.CreateDbModelComparer().Compare(before.DbModel, after.DbModel);
    var script = new DbUpgradeScriptBuilder(after.Provider).Build(changes,
      [new DbMigration("fill-nickname", DbUpgradeStage.AddColumns,
        $"UPDATE {Db.GetTableRef("Person")} SET {Db.Quote("Nickname")} = 'Roman'")]);

    await after.RunDdlAsync(script);

    Assert.AreEqual("Roman", await Db.ScalarAsync($"SELECT {Db.Quote("Nickname")} FROM {Db.GetTableRef("Person")}"),
      "the placeholder overwrote what the migration set");
  }


  // ----- ONE schema, created then upgraded then broken --------------------------------------------------------------
  //
  // Four steps that were four test methods, each creating a schema to reach the state it asserted about. They are one
  // sequence: a model becomes a database, an upgrade script takes it to the next version, a broken script leaves what
  // came before it applied, and running the original create again fails because the tables are really there.
  //
  // THE LAST STEP IS WHAT KEEPS THE OTHERS HONEST — it is the check that a green run is not green because nothing ran.
  [TestMethod]
  public async Task AModelBecomesADatabaseAndAScriptTakesItFORWARD() {
    // ----- 1. a model becomes a database ---------------------------------------------------------------------------
    var database = await CreateSchemaAsync(typeof(Ddl.IPersonV1), typeof(Ddl.IAddress));

    Assert.IsTrue(await Db.TableExistsAsync("Person"), "Person was not created");
    Assert.IsTrue(await Db.TableExistsAsync("Address"), "Address was not created");
    Assert.AreEqual(50, (await ColumnAsync("Person", "LastName")).Size);
    Assert.AreEqual(database.DbModel.Tables.Count, (await Db.LoadAsync()).Tables.Count);

    // ----- 2. and an upgrade SCRIPT runs end to end ----------------------------------------------------------------
    //
    // The whole of Phase 2 in one line each: two models, a diff, an ordered script, a server. Widening LastName is an
    // ALTER, so it is gated — the steps after it do not depend on it having run.
    if (Db.Provider.Supports(DbFeatures.AlterColumn)) {
      var after = DatabaseFor(typeof(Ddl.IPersonV2), typeof(Ddl.IAddress));
      var changes = after.Provider.CreateDbModelComparer().Compare(database.DbModel, after.DbModel);
      Assert.IsFalse(changes.HasErrors, string.Join("; ", changes.Errors));
      await after.RunDdlAsync(new DbUpgradeScriptBuilder(after.Provider).Build(changes));

      Assert.IsFalse((await ColumnAsync("Person", "Age")).IsNullable, "Age has a default and should be NOT NULL");
      Assert.AreEqual(80, (await ColumnAsync("Person", "LastName")).Size);
    }

    // ----- 3. a failure names THE command and leaves the earlier ones applied ---------------------------------------
    //
    // The non-transactional decision, observed rather than asserted about. Everything before the failure stays, which
    // is what makes "fix it and run again" the recovery.
    string[] broken = [
      $"CREATE TABLE {Db.GetTableRef("Survivor")} ({Db.Quote("Id")} varchar(40) NOT NULL)",
      "CREATE TABLE this is not sql",
    ];

    var ex = await Assert.ThrowsExactlyAsync<DataAccessException>(
      () => database.RunDdlAsync(broken, "a script that breaks"));

    StringAssert.Contains(ex.Message, "at command 2 of 2");
    StringAssert.Contains(ex.Message, "this is not sql");
    Assert.IsTrue(await Db.TableExistsAsync("Survivor"),
      "the statements before the failure should have stayed applied");

    // ----- 4. and executing the create AGAIN fails ------------------------------------------------------------------
    //
    // Not a feature — a check that the server is really doing the work.
    await Assert.ThrowsExactlyAsync<DataAccessException>(
      () => database.RunDdlAsync(new DbDdlBuilder(database.Provider).CreateModel(database.DbModel), "create again"));
  }
}
