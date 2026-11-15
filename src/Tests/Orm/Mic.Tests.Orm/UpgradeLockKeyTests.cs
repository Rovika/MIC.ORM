using Mic.Orm.Db.Model.Upgrades;

namespace Mic.Tests.Orm;

// THE LOCK KEY, which is arithmetic and needs no server. It lived in the db suite, where it cost a schema per vendor
// to run a hash twice.
[TestClass]
public class UpgradeLockKeyTests {
  [TestMethod]
  public void TheKeyIsTheSameNumberEverywhereForever() {
    // It is a hash that gets PERSISTED in effect — two machines must compute the same one, or they never queue.
    Assert.AreEqual(DbUpgradeLock.GetLockKey("LockTest"), DbUpgradeLock.GetLockKey("LockTest"));
    Assert.AreNotEqual(DbUpgradeLock.GetLockKey("A"), DbUpgradeLock.GetLockKey("B"));
  }
}
