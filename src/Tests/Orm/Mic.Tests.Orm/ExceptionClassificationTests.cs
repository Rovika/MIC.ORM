using Mic.Orm.Db;
using Mic.Orm.Db.Postgres;
using Mic.Orm.Runtime;

namespace Mic.Tests.Orm;

// SQLSTATE to a SubType, with no server to fail against — because the mapping is a table, and a table can be read.
//
// The base does this rather than each provider, and can, because SQLSTATE is a STANDARD: class 23 is "integrity
// constraint violation" everywhere, and DbException.SqlState has been on the ADO.NET base class since .NET 5. Vita
// wrote this per driver because that property did not exist then.
[TestClass]
public class ExceptionClassificationTests {
  static readonly PostgresProvider Provider = new();

  [TestMethod]
  public void SqlState_NamedOrByClassOrUnclassified() {
    // ----- the states an application acts on are named --------------------------------------------------------------
    Assert.AreEqual(DataAccessException.SubTypeUniqueViolation, Provider.SubTypeForSqlState("23505"));
    Assert.AreEqual(DataAccessException.SubTypeIntegrityViolation, Provider.SubTypeForSqlState("23503"));
    Assert.AreEqual(DataAccessException.SubTypeNotNullViolation, Provider.SubTypeForSqlState("23502"));
    Assert.AreEqual(DataAccessException.SubTypeCheckViolation, Provider.SubTypeForSqlState("23514"));
    Assert.AreEqual(DataAccessException.SubTypeDeadlock, Provider.SubTypeForSqlState("40P01"));

    // ----- an UNLISTED state falls back to its CLASS ----------------------------------------------------------------
    //
    // The classes are the standardised part; the members are not all spelled the same everywhere. Falling back to the
    // class means an unfamiliar member is still reported as the KIND of thing it is.
    Assert.AreEqual(DataAccessException.SubTypeIntegrityViolation, Provider.SubTypeForSqlState("23001"));
    Assert.AreEqual(DataAccessException.SubTypeSyntaxOrMissingObject, Provider.SubTypeForSqlState("42P01"));
    Assert.AreEqual(DataAccessException.SubTypeConnectionFailed, Provider.SubTypeForSqlState("08006"));
    Assert.AreEqual(DataAccessException.SubTypeConnectionFailed, Provider.SubTypeForSqlState("3D000"));

    // ----- and what is recognised by neither is left alone rather than guessed at ------------------------------------
    Assert.IsNull(Provider.SubTypeForSqlState("XX000"));
    Assert.IsNull(Provider.SubTypeForSqlState(null));
    Assert.IsNull(Provider.SubTypeForSqlState(""));

    // …as is a cause that was never a database failure: wrapped, carrying its source, and unclassified.
    var failure = Provider.Fail("something went wrong", new InvalidOperationException("not the database"), "ds");
    Assert.IsNull(failure.SubType);
    Assert.AreEqual("ds", failure.DatabaseAlias);
    Assert.IsInstanceOfType<InvalidOperationException>(failure.InnerException);
  }

  // ----- what a failed statement was CARRYING ------------------------------------------------------------------------

  static System.Data.Common.DbCommand CommandWith(params object?[] values) {
    var dbCommand = Provider.CreateDbCommand();
    dbCommand.CommandText = "INSERT INTO \"app\".\"Person\" VALUES (@p0)";
    for (var i = 0; i < values.Length; i++)
      Provider.AddParameter(dbCommand, $"p{i}", values[i]!);
    return dbCommand;
  }

  [TestMethod]
  public void AFailedStatementCarriesItsValuesTheirTypesAndNoMoreThanIsReadable() {
    var text = DbCommandHelper.Describe(CommandWith("Larsen", 3));

    StringAssert.Contains(text, "INSERT INTO");
    StringAssert.Contains(text, "'Larsen' (String)");
    StringAssert.Contains(text, "3 (Int32)", "the TYPE matters — a value boxed as the wrong one looks right");

    // ----- a LONG value is trimmed ----------------------------------------------------------------------------------
    //
    // So a `text` column cannot bury the sentence explaining the failure.
    var long_ = new string('x', DbCommandHelper.ValueLimit + 20);
    var trimmed = DbCommandHelper.Describe(CommandWith(long_));

    Assert.IsFalse(trimmed.Contains(long_), "the whole value should not be there");
    StringAssert.Contains(trimmed, new string('x', DbCommandHelper.ValueLimit) + "…");

    // ----- and NULLS and BINARY read as something useful --------------------------------------------------------------
    //
    // `ToString()` on an array is its type name — `System.Byte[]` — which tells a reader nothing whatever.
    var special = DbCommandHelper.Describe(CommandWith(DBNull.Value, new byte[16]));

    StringAssert.Contains(special, "= NULL");
    StringAssert.Contains(special, "byte[16]");
    Assert.IsFalse(special.Contains("System.Byte[]"));
  }
}
