using Mic.Linq.ExpressionTree;
using Mic.Linq.Translation;
using Mic.Orm;
using Mic.Orm.Db;
using Mic.Orm.Db.Postgres;
using Mic.Orm.Db.Sql;
using Mic.Orm.Extensions.MultiTenancy;
using Mic.Orm.Runtime;

namespace Mic.Tests.Orm;

// HOW THE EXTENSION FINDS THE TENANT MEMBER, at model build: [TenantId] first, then the configured name, then the
// shipped marker; a reference or a scalar; one tenant type per extension; a view without one warned about.
[TestClass]
public class MultiTenantExtensionTests {
  [Entity]
  public interface IOrder : ITenantBound {
    [PrimaryKey] Guid Id { get; set; }
    [Size(20)] string Number { get; set; }
  }

  [Entity]
  public interface IInvoice {
    [PrimaryKey] Guid Id { get; set; }
    [Size(20)] string Company { get; set; }
  }

  /// <summary>Has the configured name and opts out.</summary>
  [Entity]
  public interface IRate {
    [PrimaryKey] Guid Id { get; set; }
    [TenantId(false), Size(20)] string Company { get; set; }
  }

  /// <summary>Holds the tenant under another name and says so.</summary>
  [Entity]
  public interface ILedger {
    [PrimaryKey] Guid Id { get; set; }
    [TenantId, Size(20)] string Owner { get; set; }
  }

  [Entity]
  public interface IPlain {
    [PrimaryKey] Guid Id { get; set; }
    [Size(20)] string Name { get; set; }
  }

  /// <summary>The configured name, in another type.</summary>
  [Entity]
  public interface IStock {
    [PrimaryKey] Guid Id { get; set; }
    int Company { get; set; }
  }

  [Entity]
  public interface IDoubly {
    [PrimaryKey] Guid Id { get; set; }
    [TenantId, Size(20)] string Company { get; set; }
    [TenantId, Size(20)] string Owner { get; set; }
  }

  [Entity, View("v_sales")]
  public interface ISalesView {
    [PrimaryKey] Guid Id { get; set; }
    decimal Total { get; set; }
  }

  private sealed class TenancyApp : EntityApp {
    public TenancyApp(MultiTenantExtension extension, params Type[] entities)
        : base("Tenancy", new Version(1, 0), new EntityAppSettings { DefaultSchema = "tn" }) {
      var module = new EntityModule("Tenancy", new Version(1, 0));
      module.RegisterEntities(entities);
      AddModule(module, "tn");
      AddExtension(extension);
    }
  }

  [TestMethod]
  public void Marker_BindsThroughTheReference() {
    var extension = new MultiTenantExtension();
    var app = Init(extension, typeof(ITenant), typeof(IOrder));
    Assert.AreEqual(typeof(short), extension.TenantType, "the type of ITenant.Id, read off the key column");
    Assert.AreEqual(0, app.Model!.FindEntity("Tenant")!.Filters.Count, "the tenant table is not bound");
    var order = app.Model.FindEntity("Order")!;
    Assert.AreEqual(typeof(MultiTenantExtension), order.Filters.Single().Owner);

    // The condition is on the reference's key column: no join to the tenant table.
    AppInitHelper.CreateDatabase(app, new DbSettings(new PostgresProvider(),
      "Host=nowhere.invalid;Database=none;Username=none;Password=none"));
    var db = app.MappedModel();
    var session = new EntitySession(new OperationContext(app));
    var query = session.EntitySet<IOrder>();
    var sql = SqlBuilder.Build(QueryTranslator.Translate(ExpressionTreeScanner.Scan(query.Expression),
      ExpressionTreeConverter.Convert(query.Expression), db), db).ToString();
    StringAssert.Contains(sql, "\"Tenant_Id\" = ", sql);
    Assert.IsFalse(sql.Contains("JOIN", StringComparison.OrdinalIgnoreCase), sql);
  }

  [TestMethod]
  public void Name_BindsEveryEntityWithTheMember_AndTheAttributeOverrides() {
    var extension = new MultiTenantExtension("Company");
    var app = Init(extension, typeof(IInvoice), typeof(IRate), typeof(ILedger), typeof(IPlain));
    Assert.AreEqual(typeof(string), extension.TenantType);
    Assert.AreEqual(1, app.Model!.FindEntity("Invoice")!.Filters.Count, "found by name");
    Assert.AreEqual(0, app.Model.FindEntity("Rate")!.Filters.Count, "[TenantId(false)] excludes the entity");
    Assert.AreEqual(1, app.Model.FindEntity("Ledger")!.Filters.Count, "[TenantId] binds a member of another name");
    Assert.AreEqual(0, app.Model.FindEntity("Plain")!.Filters.Count, "no member of the name, no marker");
  }

  [TestMethod]
  public void Name_SwitchesTheMarkerOff() {
    // A model is on one path: with the name set, ITenantBound is not consulted, and a marked entity is not bound.
    var app = Init(new MultiTenantExtension("Company"), typeof(ITenant), typeof(IOrder), typeof(IInvoice));
    Assert.IsTrue(app.Initialized, app.Report());
    Assert.AreEqual(0, app.Model!.FindEntity("Order")!.Filters.Count, "the marker is not consulted");
    Assert.AreEqual(1, app.Model.FindEntity("Invoice")!.Filters.Count);
  }

  [TestMethod]
  public void OneTenantType_OrTheModelDoesNotBuild() {
    var app = Init(new MultiTenantExtension("Company"), typeof(IInvoice), typeof(IStock));
    Assert.IsFalse(app.Initialized);
    var report = app.Report();
    StringAssert.Contains(report, "one extension has one tenant type", report);
    StringAssert.Contains(report, "Int32", report);
    StringAssert.Contains(report, "String", report);
  }

  [TestMethod]
  public void TwoMarkedMembers_IsAnError() {
    var app = Init(new MultiTenantExtension(), typeof(IDoubly));
    Assert.IsFalse(app.Initialized);
    StringAssert.Contains(app.Report(), "marks both 'Company' and 'Owner'", app.Report());
  }

  [TestMethod]
  public void View_WithoutATenantMember_IsWarnedAbout() {
    var app = Init(new MultiTenantExtension(), typeof(ITenant), typeof(IOrder), typeof(ISalesView));
    Assert.IsTrue(app.Initialized, app.Report());
    Assert.IsTrue(app.Model!.BuildReport.HasWarnings, app.Report());
    StringAssert.Contains(app.Report(), "'SalesView' reads through a view and has no tenant member", app.Report());
  }

  private static TenancyApp Init(MultiTenantExtension extension, params Type[] entities) {
    var app = new TenancyApp(extension, entities);
    app.CollectingLog();
    app.TryInit();
    return app;
  }
}
