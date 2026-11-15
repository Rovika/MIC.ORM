using Mic.Orm;
using Mic.Orm.Extensions.SoftDelete;

namespace Mic.Tests.Orm;

// HOW THE EXTENSION FINDS THE DELETED MARK, at model build: [DeletedMark] first, then the configured name, then the
// shipped marker; a nullable date or a bool, nothing else.
[TestClass]
public class SoftDeleteExtensionTests {
  [Entity]
  public interface IMemo : ISoftDeleteEntity {
    [PrimaryKey] Guid Id { get; set; }
    [Size(20)] string Text { get; set; }
  }

  [Entity]
  public interface INote {
    [PrimaryKey] Guid Id { get; set; }
    bool IsDeleted { get; set; }
  }

  /// <summary>Has the configured name and opts out.</summary>
  [Entity]
  public interface IFlag {
    [PrimaryKey] Guid Id { get; set; }
    [DeletedMark(false)] bool IsDeleted { get; set; }
  }

  /// <summary>Holds the mark under another name and says so.</summary>
  [Entity]
  public interface IArchive {
    [PrimaryKey] Guid Id { get; set; }
    [DeletedMark] DateTime? RemovedOn { get; set; }
  }

  [Entity]
  public interface IPlain {
    [PrimaryKey] Guid Id { get; set; }
    [Size(20)] string Name { get; set; }
  }

  /// <summary>The configured name, in a type that is no mark.</summary>
  [Entity]
  public interface IStatus {
    [PrimaryKey] Guid Id { get; set; }
    int IsDeleted { get; set; }
  }

  [Entity]
  public interface IDoubly {
    [PrimaryKey] Guid Id { get; set; }
    [DeletedMark] bool IsDeleted { get; set; }
    [DeletedMark] DateTime? DeletedOn { get; set; }
  }

  private sealed class SoftDeleteApp : EntityApp {
    public SoftDeleteApp(SoftDeleteExtension extension, params Type[] entities)
        : base("SoftDelete", new Version(1, 0), new EntityAppSettings { DefaultSchema = "sd" }) {
      var module = new EntityModule("SoftDelete", new Version(1, 0));
      module.RegisterEntities(entities);
      AddModule(module, "sd");
      AddExtension(extension);
    }
  }

  [TestMethod]
  public void Marker_BindsDeletedOn() {
    var app = Init(new SoftDeleteExtension(), typeof(IMemo), typeof(IPlain));
    Assert.IsTrue(app.Initialized, app.Report());
    Assert.AreEqual(typeof(SoftDeleteExtension), app.Model!.FindEntity("Memo")!.Filters.Single().Owner);
    Assert.AreEqual(0, app.Model.FindEntity("Plain")!.Filters.Count, "no marker, no filter");
  }

  [TestMethod]
  public void Name_BindsEveryEntityWithTheMember_AndTheAttributeOverrides() {
    var app = Init(new SoftDeleteExtension("IsDeleted"),
      typeof(INote), typeof(IFlag), typeof(IArchive), typeof(IPlain));
    Assert.IsTrue(app.Initialized, app.Report());
    Assert.AreEqual(1, app.Model!.FindEntity("Note")!.Filters.Count, "found by name, a bool");
    Assert.AreEqual(0, app.Model.FindEntity("Flag")!.Filters.Count, "[DeletedMark(false)] excludes the entity");
    Assert.AreEqual(1, app.Model.FindEntity("Archive")!.Filters.Count, "[DeletedMark] binds a date of another name");
    Assert.AreEqual(0, app.Model.FindEntity("Plain")!.Filters.Count);
  }

  [TestMethod]
  public void Name_SwitchesTheMarkerOff() {
    var app = Init(new SoftDeleteExtension("IsDeleted"), typeof(IMemo), typeof(INote));
    Assert.IsTrue(app.Initialized, app.Report());
    Assert.AreEqual(0, app.Model!.FindEntity("Memo")!.Filters.Count, "the marker is not consulted");
    Assert.AreEqual(1, app.Model.FindEntity("Note")!.Filters.Count);
  }

  [TestMethod]
  public void AMarkOfAnotherType_DoesNotBuild() {
    var app = Init(new SoftDeleteExtension("IsDeleted"), typeof(IStatus));
    Assert.IsFalse(app.Initialized);
    StringAssert.Contains(app.Report(), "a deleted mark is a nullable DateTime or a bool", app.Report());
  }

  [TestMethod]
  public void TwoMarkedMembers_IsAnError() {
    var app = Init(new SoftDeleteExtension(), typeof(IDoubly));
    Assert.IsFalse(app.Initialized);
    StringAssert.Contains(app.Report(), "marks both 'IsDeleted' and 'DeletedOn'", app.Report());
  }

  private static SoftDeleteApp Init(SoftDeleteExtension extension, params Type[] entities) {
    var app = new SoftDeleteApp(extension, entities);
    app.CollectingLog();
    app.TryInit();
    return app;
  }
}
