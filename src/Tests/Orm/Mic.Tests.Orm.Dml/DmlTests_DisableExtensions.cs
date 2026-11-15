using Mic.Linq;
using Mic.Orm;
using Mic.Orm.Extensions.MultiTenancy;
using Mic.Orm.Extensions.SoftDelete;
using Mic.Orm.Runtime;

namespace Mic.Tests.Orm.Db;

// AN EXTENSION DISABLED ON A SESSION: its filters out of every read of the session, its write hooks skipping the
// session's saves. The switch is per session, for a job that works outside what the extension enforces: a purge that
// hard-deletes soft-deleted rows, a migration that moves rows between tenants. There is no per-query form.
public abstract partial class DmlTests {
  [TestMethod]
  public async Task DisableExtension_DropsFiltersAndWriteHooksForTheSession() {
    var tag = Guid.NewGuid().ToString("N")[..8];

    // ----- soft delete: the deleted row is seen, and a delete is a delete, in the disabling session ---------------------
    var writer = NewContext(App).OpenSession();
    var kept = writer.NewEntity<Dml.IMemo>();
    kept.Id = Guid.NewGuid();
    kept.Text = tag + "-kept";
    var gone = writer.NewEntity<Dml.IMemo>();
    gone.Id = Guid.NewGuid();
    gone.Text = tag + "-gone";
    await writer.SaveChangesAsync();
    writer.DeleteEntity(gone);
    await writer.SaveChangesAsync();
    var goneId = gone.Id;

    var reader = NewContext(App).OpenSession();
    Assert.AreEqual(1, await reader.EntitySet<Dml.IMemo>().Where(m => m.Text.StartsWith(tag)).CountAsync(),
      "the filter hides the stamped row");

    var purge = NewContext(App).OpenSession();
    purge.DisableExtension<SoftDeleteExtension>();
    var memos = purge.EntitySet<Dml.IMemo>().Where(m => m.Text.StartsWith(tag));
    Assert.AreEqual(2, await memos.CountAsync(), "every row, the stamped one too");
    var stamped = await purge.GetEntityAsync<Dml.IMemo>(goneId);
    Assert.IsNotNull(stamped.DeletedOn, "read by key without the filter");
    purge.DeleteEntity(stamped);
    await purge.SaveChangesAsync();
    StringAssert.Contains(purge.GetLastCommands().Single().Sql, "DELETE", StringComparison.OrdinalIgnoreCase);
    Assert.AreEqual(1, await memos.CountAsync(), "the row is gone from the table");

    var normal = NewContext(App).OpenSession();
    normal.DeleteEntity(await normal.GetEntityAsync<Dml.IMemo>(kept.Id));
    await normal.SaveChangesAsync();
    StringAssert.Contains(normal.GetLastCommands().Single().Sql, "UPDATE", StringComparison.OrdinalIgnoreCase);

    // ----- tenancy: a row saved for another tenant, and read back under that tenant --------------------------------------
    var tenant = Guid.NewGuid();
    var migration = NewContext(App).OpenSession();
    migration.DisableExtension<MultiTenantExtension>();
    var job = migration.NewEntity<Dml.IJob>();
    job.Id = Guid.NewGuid();
    job.TenantId = tenant;
    job.Title = tag;
    await migration.SaveChangesAsync();
    Assert.AreEqual(tenant, job.TenantId, "kept as written: no context tenant to stamp, no refusal");
    Assert.AreEqual(1, await migration.EntitySet<Dml.IJob>().Where(j => j.Title == tag).CountAsync(), "seen without the filter");

    var tenantContext = NewContext(App);
    tenantContext.SetValue(((DmlApp)App).Tenancy.TenantIdKey, tenant);
    Assert.AreEqual(1, await tenantContext.OpenSession().EntitySet<Dml.IJob>().Where(j => j.Title == tag).CountAsync(),
      "the tenant sees its row");
    Assert.AreEqual(0, await NewContext(App).OpenSession().EntitySet<Dml.IJob>().Where(j => j.Title == tag).CountAsync(),
      "a context with no tenant and the extension on sees nothing");

    // ----- one extension at a time: with tenancy disabled, the soft-delete filter stays ---------------------------------
    Assert.AreEqual(0, await migration.EntitySet<Dml.IMemo>().Where(m => m.Text.StartsWith(tag)).CountAsync(),
      "the one row left is stamped, and the soft-delete filter still hides it");

    // ----- all extensions, a stub and a list loaded without the filters --------------------------------------------------
    var all = NewContext(App).OpenSession();
    all.DisableExtensions();
    var stub = all.GetEntity<Dml.IMemo>(kept.Id, LoadFlags.Stub);
    Assert.IsNotNull(stub.DeletedOn, "the stub's row loaded without the filter");
    var byName = all.From("Job").Where(row => row.Member("Title").Equal(QueryMaker.Constant(tag)));
    Assert.AreEqual(1, (await all.ExecuteQueryAsync(byName)).Count, "a query built from names, without the filters");

    // ----- the query cache: a disabling session's queries are translated each time and kept nowhere ---------------------
    var cache = ((EntitySession)all).Database.QueryCache;
    var entries = cache.Count;
    var jobs = all.EntitySet<Dml.IJob>().Where(j => j.Title == tag && j.Id != Guid.Empty);
    Assert.AreEqual(1, await jobs.CountAsync());
    Assert.AreEqual(1, await jobs.CountAsync());
    Assert.AreEqual(entries, cache.Count, "no entry for the shape");

    // ----- the entity cache: a cached entity is read from the server in a disabling session ------------------------------
    var colorTenant = Guid.NewGuid();
    var colorContext = NewContext(App);
    colorContext.SetValue(((DmlApp)App).Tenancy.TenantIdKey, colorTenant);
    var colorWriter = colorContext.OpenSession();
    var color = colorWriter.NewEntity<Dml.IColor>();
    color.Id = Guid.NewGuid();
    color.Name = tag + "-teal";
    await colorWriter.SaveChangesAsync();
    var colorReader = colorContext.OpenSession();
    Assert.AreEqual(1, await colorReader.EntitySet<Dml.IColor>().Where(c => c.Name == tag + "-teal").CountAsync(),
      "the tenant's block is filled");
    var spanning = NewContext(App).OpenSession();
    spanning.DisableExtensions();
    Assert.AreEqual(1, await spanning.EntitySet<Dml.IColor>().Where(c => c.Name == tag + "-teal").CountAsync(),
      "no tenant in the context, and the row is seen: not the empty block of no tenant");
    StringAssert.Contains(spanning.GetLastCommands().Single().Sql, "SELECT", StringComparison.OrdinalIgnoreCase);
    var teal = await spanning.GetEntityAsync<Dml.IColor>(color.Id);
    Assert.AreEqual(colorTenant, teal.TenantId, "by key, from the server too");
  }
}
