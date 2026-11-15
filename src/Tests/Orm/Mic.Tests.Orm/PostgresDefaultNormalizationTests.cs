using Mic.Orm.Db;
using Mic.Orm.Db.Postgres;

namespace Mic.Tests.Orm;

// PostgreSQL appends an explicit cast to a literal default, so `'none'` comes back as `'none'::character varying`.
// Undoing that is what stops the upgrade wanting to alter the same column on every startup — and undoing it too
// eagerly would corrupt defaults that legitimately contain a cast. No server needed to prove either.
[TestClass]
public class PostgresDefaultNormalizationTests {
  sealed class Probe(DbSettings settings) : PostgresModelLoader(settings) {
    public string? Run(string? expression) => NormalizeDefault(expression);
  }

  static readonly Probe Loader = new(new DbSettings(new PostgresProvider(), "Host=nowhere.invalid;Database=x"));

  [TestMethod]
  public void ACastOnAWholeLiteralIsUnwrapped() {
    Assert.AreEqual("'none'", Loader.Run("'none'::character varying"));
    Assert.AreEqual("'none'", Loader.Run("'none'::character varying(20)"));
    Assert.AreEqual("'x'", Loader.Run("'x'::text"));
    Assert.AreEqual("''''", Loader.Run("''''::text"));           // a literal containing one quote, doubled
  }

  [TestMethod]
  public void ACastINSIDEAnExpressionIsLeftALONE() {
    // The case that makes the narrow pattern worth having: unwrapping here would destroy the default outright.
    Assert.AreEqual("nextval('things_id_seq'::regclass)", Loader.Run("nextval('things_id_seq'::regclass)"));
  }

  [TestMethod]
  public void UnrewrittenDefault_IsUntouched() {
    Assert.AreEqual("0", Loader.Run("0"));
    Assert.AreEqual("true", Loader.Run("true"));
    Assert.AreEqual("now()", Loader.Run("now()"));
    Assert.AreEqual("'none'", Loader.Run("'none'"));
    Assert.IsNull(Loader.Run(null));
  }
}
