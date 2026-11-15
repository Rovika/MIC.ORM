using Mic.Linq;
using Mic.Orm;
using Mic.Orm.Extensions.MultiTenancy;
using Mic.Orm.Logging;
using Mic.Orm.Runtime;

namespace Mic.Tests.Orm.Db;

// THE EXTENSIONS, as an application uses them: added to `DmlApp` at startup, applying to the entities that carry
// their markers and to nothing else.
public abstract partial class DmlTests {
  [TestMethod]
  public async Task SoftDelete_StampsTheRowAndHidesIt() {
    await using var writer = NewSession();
    var kept = writer.NewEntity<Dml.IMemo>();
    kept.Id = Guid.NewGuid();
    kept.Text = "kept";
    var gone = writer.NewEntity<Dml.IMemo>();
    gone.Id = Guid.NewGuid();
    gone.Text = "gone";
    await writer.SaveChangesAsync();
    var keptId = kept.Id;
    var goneId = gone.Id;

    // ----- a delete is an UPDATE stamping the row, and the record stays tracked -------------------------------------
    writer.LogMessage("soft delete: the memo is deleted, and the row is stamped instead");
    writer.DeleteEntity(gone);
    await writer.SaveChangesAsync();
    StringAssert.Contains(writer.LastCommands.Single().Sql, "UPDATE", "a soft delete writes an update");
    Assert.IsNotNull(gone.DeletedOn, "stamped from the application's clock");
    Assert.AreEqual(EntityStatus.Loaded, ((EntityBase)gone).Record.Status, "saved as a modified row, and kept");
    Assert.IsNotNull(await ScalarAsync("Memo", "DeletedOn", goneId), "the row is still there, stamped");
    Assert.IsNull(kept.DeletedOn);

    // ----- a fresh session does not see it: a query, and a by-key load ----------------------------------------------
    await using var reader = NewSession();
    var visible = await reader.EntitySet<Dml.IMemo>().Where(m => m.Id == keptId || m.Id == goneId).ToListAsync();
    Assert.AreEqual(1, visible.Count, "the filter hides the stamped row");
    Assert.AreEqual("kept", visible[0].Text);
    Assert.IsNull(await reader.GetEntityAsync<Dml.IMemo>(goneId), "a by-key load is filtered too");
    Assert.IsNotNull(await reader.GetEntityAsync<Dml.IMemo>(keptId));

    // ----- an entity without the marker is deleted for real -----------------------------------------------------------
    var address = reader.NewEntity<Dml.IAddress>();
    address.Id = Guid.NewGuid();
    address.City = "soft-delete-" + Guid.NewGuid().ToString("N")[..8];
    await reader.SaveChangesAsync();
    reader.DeleteEntity(address);
    await reader.SaveChangesAsync();
    StringAssert.Contains(reader.LastCommands.Single().Sql, "DELETE", "no marker, no stamp");
  }

  [TestMethod]
  public async Task MultiTenancy_ReadsAndWritesAsTheTenant() {
    var tenantA = Guid.NewGuid();
    var tenantB = Guid.NewGuid();
    IEntitySession For(Guid? tenant) {
      var context = NewContext(App);
      context.SetValue(((DmlApp)App).Tenancy.TenantIdKey, tenant);
      return context.OpenSession();
    }

    // ----- a new row is stamped with the context's tenant at save --------------------------------------------------
    var sessionA = For(tenantA);
    var jobA = sessionA.NewEntity<Dml.IJob>();
    jobA.Id = Guid.NewGuid();
    jobA.Title = "a";
    await sessionA.SaveChangesAsync();
    Assert.AreEqual(tenantA, jobA.TenantId, "stamped from the context");
    var sessionB = For(tenantB);
    var jobB = sessionB.NewEntity<Dml.IJob>();
    jobB.Id = Guid.NewGuid();
    jobB.Title = "b";
    await sessionB.SaveChangesAsync();
    var jobAId = jobA.Id;
    var jobBId = jobB.Id;

    // ----- each tenant sees its own rows and nothing else: a query, and a by-key load ------------------------------
    var readerA = For(tenantA);
    var seenByA = await readerA.EntitySet<Dml.IJob>().Where(j => j.Id == jobAId || j.Id == jobBId).ToListAsync();
    Assert.AreEqual("a", seenByA.Single().Title);
    Assert.IsNull(await readerA.GetEntityAsync<Dml.IJob>(jobBId), "the other tenant's row is not loadable by key");
    var readerB = For(tenantB);
    Assert.AreEqual("b", (await readerB.EntitySet<Dml.IJob>().Where(j => j.Id == jobAId || j.Id == jobBId).ToListAsync()).Single().Title);
    var nobody = For(null);
    Assert.AreEqual(0, (await nobody.EntitySet<Dml.IJob>().Where(j => j.Id == jobAId || j.Id == jobBId).ToListAsync()).Count,
      "a context with no tenant sees no rows");

    // ----- a row given another tenant's id is REFUSED at save, with the record's other faults ----------------------
    var stray = readerA.NewEntity<Dml.IJob>();
    stray.Id = Guid.NewGuid();
    stray.Title = "stray";
    stray.TenantId = tenantB;
    var fault = Assert.ThrowsExactly<ClientFaultException>(readerA.ValidateChanges).Faults.Single();
    Assert.AreEqual("TenantBoundary", fault.Code);
    Assert.AreEqual("TenantId", fault.MemberName);
    readerA.DeleteEntity(stray);
    // …and a row saved by a context with no tenant, itself naming none, is refused too.
    var orphan = nobody.NewEntity<Dml.IJob>();
    orphan.Id = Guid.NewGuid();
    orphan.Title = "orphan";
    Assert.AreEqual("TenantBoundary", Assert.ThrowsExactly<ClientFaultException>(nobody.ValidateChanges).Faults.Single().Code);
  }

  [TestMethod]
  public async Task LegacyCompanyCode_IsATenantByName() {
    // THE LEGACY CASE: a string code in a column named `Company`, no marker on the entity, the context naming the
    // company under `Company` — the extension configured with the member's name and the key, nothing derived.
    var key = ((DmlApp)App).LegacyTenancy.TenantIdKey;
    Assert.AreEqual("Company", key);
    IEntitySession For(string? company) {
      var context = NewContext(App);
      context.SetValue(key, company);
      return context.OpenSession();
    }
    var acme = "ACME-" + Guid.NewGuid().ToString("N")[..6];
    var globex = "GLOBEX-" + Guid.NewGuid().ToString("N")[..6];

    var acmeSession = For(acme);
    var orderA = acmeSession.NewEntity<Dml.ILegacyOrder>();
    orderA.Id = Guid.NewGuid();
    orderA.Product = "anvil";
    await acmeSession.SaveChangesAsync();
    Assert.AreEqual(acme, orderA.Company, "stamped with the code from the context");
    var globexSession = For(globex);
    var orderG = globexSession.NewEntity<Dml.ILegacyOrder>();
    orderG.Id = Guid.NewGuid();
    orderG.Product = "globe";
    await globexSession.SaveChangesAsync();
    var orderAId = orderA.Id;
    var orderGId = orderG.Id;

    var readerA = For(acme);
    var seen = await readerA.EntitySet<Dml.ILegacyOrder>().Where(o => o.Id == orderAId || o.Id == orderGId).ToListAsync();
    Assert.AreEqual("anvil", seen.Single().Product, "the company's own orders and no other's");
    Assert.IsNull(await readerA.GetEntityAsync<Dml.ILegacyOrder>(orderGId));
    var stray = readerA.NewEntity<Dml.ILegacyOrder>();
    stray.Id = Guid.NewGuid();
    stray.Product = "stray";
    stray.Company = globex;
    var fault = Assert.ThrowsExactly<ClientFaultException>(readerA.ValidateChanges).Faults.Single();
    Assert.AreEqual("TenantBoundary", fault.Code);
    Assert.AreEqual("Company", fault.MemberName);
    readerA.DeleteEntity(stray);
    var none = For(null);
    Assert.AreEqual(0, (await none.EntitySet<Dml.ILegacyOrder>().Where(o => o.Id == orderAId || o.Id == orderGId).ToListAsync()).Count,
      "no company named, nothing seen");
  }

  [TestMethod]
  public async Task LegacyDeletedFlag_IsASoftDeleteByName() {
    // THE LEGACY CASE: an `IsDeleted` bit, no marker, no date — the extension configured with the member's name, the
    // bool deciding that the live rows are `false` and a delete writes `true`.
    await using var writer = NewSession();
    var kept = writer.NewEntity<Dml.ILegacyNote>();
    kept.Id = Guid.NewGuid();
    kept.Text = "kept";
    var gone = writer.NewEntity<Dml.ILegacyNote>();
    gone.Id = Guid.NewGuid();
    gone.Text = "gone";
    await writer.SaveChangesAsync();
    var keptId = kept.Id;
    var goneId = gone.Id;

    writer.DeleteEntity(gone);
    await writer.SaveChangesAsync();
    StringAssert.Contains(writer.LastCommands.Single().Sql, "UPDATE", "a flagged soft delete writes an update");
    Assert.IsTrue(gone.IsDeleted, "the flag is what a delete writes");
    Assert.IsFalse(kept.IsDeleted);
    Assert.AreEqual(EntityStatus.Loaded, ((EntityBase)gone).Record.Status);

    await using var reader = NewSession();
    var visible = await reader.EntitySet<Dml.ILegacyNote>().Where(n => n.Id == keptId || n.Id == goneId).ToListAsync();
    Assert.AreEqual("kept", visible.Single().Text, "the flagged row is hidden");
    Assert.IsNull(await reader.GetEntityAsync<Dml.ILegacyNote>(goneId), "by key too");
  }
}
