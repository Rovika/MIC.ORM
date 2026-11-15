using Mic.Linq;
using Mic.Orm;

namespace Mic.Tests.Orm.Db;

// A VIEW ENTITY: rows the server derives, read through an entity as any table's are, and never written.
public abstract partial class DmlTests {
  [TestMethod]
  public async Task View_ReadsDerivedRowsAndRefusesWrites() {
    var tag = "view-" + Guid.NewGuid().ToString("N")[..6];
    await using var session = NewSession();
    var address = session.NewEntity<Dml.IAddress>();
    address.Id = Guid.NewGuid();
    address.City = tag;
    for (var i = 0; i < 3; i++) {
      var person = session.NewEntity<Dml.IPerson>();
      person.Id = Guid.NewGuid();
      person.LastName = tag + i;
      person.Address = address;
    }
    await session.SaveChangesAsync();

    // ----- defined by a typed query: each address with its resident count ------------------------------------------
    await using var reader = NewSession();
    var summaries = await reader.EntitySet<Dml.IAddressView>().Where(v => v.City == tag).ToListAsync();
    Assert.AreEqual(1, summaries.Count);
    Assert.AreEqual(address.Id, summaries[0].Id);
    Assert.AreEqual(3, summaries[0].ResidentCount);
    var byKey = await reader.GetEntityAsync<Dml.IAddressView>(address.Id);
    Assert.AreSame(summaries[0], byKey, "a view's rows are tracked by their key like any entity's");

    // ----- defined by SQL, one text per server -------------------------------------------------------------------
    var names = await reader.EntitySet<Dml.IPersonName>()
      .Where(n => n.LastName.StartsWith(tag)).OrderBy(n => n.LastName).ToListAsync();
    Assert.AreEqual(3, names.Count);
    Assert.AreEqual(tag + "0", names[0].LastName);

    // ----- read-only: no new row, no delete, no change ----------------------------------------------------------
    Assert.ThrowsExactly<InvalidOperationException>(() => reader.NewEntity<Dml.IAddressView>());
    Assert.ThrowsExactly<InvalidOperationException>(() => reader.DeleteEntity(summaries[0]));
    Assert.ThrowsExactly<InvalidOperationException>(() => summaries[0].City = "elsewhere");
    // A statement over the view is refused at translation, whatever the server would say to it.
    await Assert.ThrowsExactlyAsync<NotSupportedException>(() =>
      reader.EntitySet<Dml.IAddressView>().ExecuteDeleteAsync());
    await Assert.ThrowsExactlyAsync<NotSupportedException>(() =>
      reader.EntitySet<Dml.IAddressView>().Select(v => new { City = "x" }).ExecuteUpdateAsync<Dml.IAddressView>());
  }

  [TestMethod]
  public async Task View_WithoutAKey_ReadsRowsUntracked() {
    var tag = "nokey-" + Guid.NewGuid().ToString("N")[..6];
    await using var session = NewSession();
    // The same name and rank twice: no column of the view tells the two rows apart.
    for (var i = 0; i < 2; i++) {
      var person = session.NewEntity<Dml.IPerson>();
      person.Id = Guid.NewGuid();
      person.LastName = tag;
      person.Rank = 7;
    }
    await session.SaveChangesAsync();

    // ----- two rows alike in every column are two records, and a second read makes new ones -----------------------
    await using var reader = NewSession();
    var rows = await reader.EntitySet<Dml.INameRank>().Where(r => r.LastName == tag).ToListAsync();
    Assert.AreEqual(2, rows.Count);
    Assert.AreNotSame(rows[0], rows[1]);
    Assert.AreEqual(7, rows[1].Rank);
    var again = await reader.EntitySet<Dml.INameRank>().Where(r => r.LastName == tag).ToListAsync();
    Assert.AreNotSame(rows[0], again[0], "a row with no key is not found in the session");

    // ----- no identity: nothing to look a row up by, and one row of an unordered query cannot be chosen -----------
    await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => reader.GetEntityAsync<Dml.INameRank>(tag));
    await Assert.ThrowsExactlyAsync<NotSupportedException>(() =>
      reader.EntitySet<Dml.INameRank>().Where(r => r.LastName == tag).FirstAsync());
    var ordered = await reader.EntitySet<Dml.INameRank>().Where(r => r.LastName == tag)
      .OrderBy(r => r.Rank).FirstAsync();
    Assert.AreEqual(tag, ordered.LastName);

    // ----- read-only like any view -------------------------------------------------------------------------------
    Assert.ThrowsExactly<InvalidOperationException>(() => rows[0].Rank = 8);

    // ----- and nothing for a query to compare, join or group rows by: each is refused, naming the view ------------
    var view = reader.EntitySet<Dml.INameRank>();
    await AssertRefusedAsync(from a in view from b in view where a == b select a.LastName, "compare");
    await AssertRefusedAsync(view.Join(view, a => a, b => b, (a, b) => a.LastName), "join");
    await AssertRefusedAsync(view.GroupBy(r => r).Select(g => g.Count()), "group");
    await AssertRefusedAsync(view.GroupBy(r => new { r, r.Rank }).Select(g => g.Count()), "group");

    // By its members such a row groups as any other does.
    var byRank = await view.Where(r => r.LastName == tag).GroupBy(r => r.Rank)
      .Select(g => new { Rank = g.Key, Count = g.Count() }).ToListAsync();
    Assert.AreEqual(2, byRank.Single().Count);

    // ----- local functions
    static async Task AssertRefusedAsync<T>(IQueryable<T> query, string use) {
      var refused = await Assert.ThrowsExactlyAsync<NotSupportedException>(() => query.ToListAsync());
      StringAssert.Contains(refused.Message, $"'NameRank' has no primary key to {use} its rows by");
    }
  }
}
