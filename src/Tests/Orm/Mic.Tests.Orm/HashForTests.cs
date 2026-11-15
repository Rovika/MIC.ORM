using Mic.Orm;
using Mic.Orm.Model;

namespace Mic.Tests.Orm;

// A MEMBER THAT HOLDS A HASH OF ANOTHER MEMBER'S TEXT, without a server: the hash follows every assignment of the
// text, it is the value the application computes for a query, and what cannot be hashed is refused when the model
// is built.
[TestClass]
public class HashForTests {
  public interface IAccount {
    [PrimaryKey] Guid Id { get; set; }
    [Size(200)] string Email { get; set; }
    [HashFor(nameof(Email)), Index] int EmailHash { get; set; }
    [Unlimited] string? Url { get; set; }
    [HashFor(nameof(Url))] long UrlHash { get; set; }
  }

  public interface IHashOfWrongType {
    [PrimaryKey] Guid Id { get; set; }
    [Size(50)] string Name { get; set; }
    [HashFor(nameof(Name))] short NameHash { get; set; }
  }

  public interface IHashOfANumber {
    [PrimaryKey] Guid Id { get; set; }
    int Count { get; set; }
    [HashFor(nameof(Count))] int CountHash { get; set; }
  }

  public interface IHashOfNothing {
    [PrimaryKey] Guid Id { get; set; }
    [HashFor("Missing")] int MissingHash { get; set; }
  }

  public interface IHashedTwice {
    [PrimaryKey] Guid Id { get; set; }
    [Size(50)] string Name { get; set; }
    [HashFor(nameof(Name))] int NameHash { get; set; }
    [HashFor(nameof(Name))] long NameHash64 { get; set; }
  }

  static EntityApp NewApp(Type entity) {
    var app = new EntityApp("Hashing", new Version(1, 0), new EntityAppSettings { DefaultSchema = "app" });
    var module = new EntityModule("Accounts", new Version(1, 0));
    module.RegisterEntities(entity);
    app.AddModule(module);
    app.Settings.IncludeDbInfoModule = false;
    app.TryInit();
    return app;
  }

  [TestMethod]
  public void HashMember_FollowsEveryAssignmentOfTheText() {
    var app = NewApp(typeof(IAccount));
    Assert.IsTrue(app.Initialized, app.Report());
    var entity = app.Model!.FindEntity(typeof(IAccount))!;
    Assert.AreSame(entity.GetMember("EmailHash"), entity.GetMember("Email").HashMember);

    var session = app.WithSqlite().OpenSession();
    var account = session.NewEntity<IAccount>();
    Assert.AreEqual(0, account.EmailHash, "nothing is hashed until the text is assigned");

    // ----- the hash is the one the application computes for a query, over the trimmed text -------------------------
    account.Email = "  ann@example.com ";
    Assert.AreNotEqual(0, account.EmailHash);
    Assert.AreEqual(app.ComputeStableHash("ann@example.com"), account.EmailHash);
    account.Url = "https://example.com/a/long/address";
    Assert.AreEqual(app.ComputeStableHash64("https://example.com/a/long/address"), account.UrlHash);

    // ----- a new text is a new hash; none is 0 ------------------------------------------------------------------------
    var first = account.EmailHash;
    account.Email = "bob@example.com";
    Assert.AreNotEqual(first, account.EmailHash);
    Assert.AreNotEqual(app.ComputeStableHash("Bob@example.com"), account.EmailHash, "the case of the text counts");
    account.Url = null;
    Assert.AreEqual(0L, account.UrlHash);
    account.Url = "   ";
    Assert.AreEqual(0L, account.UrlHash);
    Assert.AreEqual(0, app.ComputeStableHash(null));

    // ----- the algorithm, pinned: a stored hash must mean the same in every later version ---------------------------
    //
    // FNV-1a, 64 bits, over the text's UTF-16 units; "a" is the published test vector. An int holds the two halves
    // folded together.
    const long hashOfA = unchecked((long)0xaf63dc4c8601ec8cUL);
    Assert.AreEqual(hashOfA, app.ComputeStableHash64("a"));
    Assert.AreEqual(unchecked((int)(0xaf63dc4cU ^ 0x8601ec8cU)), app.ComputeStableHash("a"));
  }

  [TestMethod]
  public void HashFor_RefusesWhatItCannotHash() {
    AssertRefused(typeof(IHashOfWrongType), "a hash is an int or a long");
    AssertRefused(typeof(IHashOfANumber), "names no text member");
    AssertRefused(typeof(IHashOfNothing), "names no text member");
    AssertRefused(typeof(IHashedTwice), "is hashed into");

    // ----- local functions
    static void AssertRefused(Type entity, string expected) {
      var app = NewApp(entity);
      Assert.IsFalse(app.Initialized, entity.Name + " was built");
      StringAssert.Contains(app.Report(), expected);
    }
  }
}
