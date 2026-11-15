using Mic.Linq;
using Mic.Orm;
using Mic.Orm.Extensions.MultiTenancy;
using Mic.Orm.Logging;
using Mic.Orm.Runtime;

namespace Mic.Tests.Orm.Db;

// THE SHIPPED MARKER: an entity carrying ITenantBound is bound through its `Tenant` reference, and the extension works
// on the reference's key column: the filter compares it, the stamp writes the tenant's id into it without loading the
// tenant, the boundary check reads it. The tenant table itself is not bound.
public abstract partial class DmlTests {
  [TestMethod]
  public async Task TenantBound_IsFilteredAndStampedThroughTheReference() {
    var key = ((DmlApp)App).BoundTenancy.TenantIdKey;
    Assert.AreEqual("Tenant", key);
    Assert.AreEqual(typeof(short), ((DmlApp)App).BoundTenancy.TenantType, "the tenant's type is the key column's");

    // ----- the tenants, written under a context that names none: ITenant is not bound ---------------------------
    var setup = NewContext(App).OpenSession();
    var acme = setup.NewEntity<ITenant>();
    acme.Id = NewTenantId();
    acme.Name = "Acme";
    var globex = setup.NewEntity<ITenant>();
    globex.Id = NewTenantId();
    globex.Name = "Globex";
    await setup.SaveChangesAsync();
    IEntitySession For(object? tenant) {
      var context = NewContext(App);
      context.SetValue(key, tenant);
      return context.OpenSession();
    }

    // ----- a new row is stamped with the context's tenant, into the reference's key column -----------------------
    var sessionA = For(acme.Id);
    var bookingA = sessionA.NewEntity<Dml.IBooking>();
    bookingA.Id = Guid.NewGuid();
    bookingA.Title = "cut";
    await sessionA.SaveChangesAsync();
    Assert.AreEqual(acme.Id, bookingA.Tenant.Id, "stamped from the context, read back through the reference");
    var sessionB = For(globex.Id);
    var bookingB = sessionB.NewEntity<Dml.IBooking>();
    bookingB.Id = Guid.NewGuid();
    bookingB.Title = "colour";
    await sessionB.SaveChangesAsync();
    var bookingAId = bookingA.Id;
    var bookingBId = bookingB.Id;

    // ----- each tenant sees its own rows: the condition on the key column, no join to the tenant -----------------
    var readerA = For(acme.Id);
    var seen = await readerA.EntitySet<Dml.IBooking>().Where(b => b.Id == bookingAId || b.Id == bookingBId).ToListAsync();
    Assert.AreEqual("cut", seen.Single().Title, "the tenant's own rows and no other's");
    StringAssert.Contains(readerA.GetLastCommands().Single().Sql, "Tenant_Id", "the key column, not the tenant table");
    Assert.IsNull(await readerA.GetEntityAsync<Dml.IBooking>(bookingBId), "the other tenant's row is not loadable by key");

    // ----- the context's value is converted to the column's type: an int names the same tenant -------------------
    var asInt = For((int)acme.Id);
    Assert.AreEqual(1, (await asInt.EntitySet<Dml.IBooking>().Where(b => b.Id == bookingAId || b.Id == bookingBId).ToListAsync()).Count,
      "an int in the context converts to the column's short");

    // ----- a row given another tenant is refused at save, the fault on the reference member ----------------------
    var stray = readerA.NewEntity<Dml.IBooking>();
    stray.Id = Guid.NewGuid();
    stray.Title = "stray";
    stray.Tenant = (await readerA.GetEntityAsync<ITenant>(globex.Id))!;
    var fault = Assert.ThrowsExactly<ClientFaultException>(readerA.ValidateChanges).Faults.Single();
    Assert.AreEqual("TenantBoundary", fault.Code);
    Assert.AreEqual("Tenant", fault.MemberName);
    readerA.DeleteEntity(stray);

    // ----- no tenant named: nothing seen ------------------------------------------------------------------------------
    var none = For(null);
    Assert.AreEqual(0, (await none.EntitySet<Dml.IBooking>().Where(b => b.Id == bookingAId || b.Id == bookingBId).ToListAsync()).Count,
      "a context with no tenant sees no rows");
  }

  /// <summary>Returns a tenant id unlikely to be in the table from an earlier run.</summary>
  private static short NewTenantId() => (short)Random.Shared.Next(1, short.MaxValue);
}
