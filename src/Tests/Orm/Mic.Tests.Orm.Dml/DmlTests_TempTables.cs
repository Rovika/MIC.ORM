using Mic.Linq;
using Mic.Orm;

namespace Mic.Tests.Orm.Db;

// A TEMP TABLE: rows the session stages on its connection, read like any entity set, joined against, the source of a
// scheduled update, and gone with the session; or created empty and filled by set-based inserts from queries.
public abstract partial class DmlTests {
  [TestMethod]
  public async Task TempTable_IsFilledJoinedAndWrittenThrough() {
    var tag = "tmp-" + Guid.NewGuid().ToString("N")[..6];
    await using var session = NewSession();
    Dml.IPerson Person(int rank) {
      var person = session.NewEntity<Dml.IPerson>();
      person.Id = Guid.NewGuid();
      person.LastName = $"{tag}-{rank}";
      person.Rank = rank;
      return person;
    }
    var first = Person(1);
    var second = Person(2);
    var third = Person(3);
    await session.SaveChangesAsync();

    // ----- registered with its rows, created and filled by the first query that reads it -------------------------
    var staged = session.CreateTempTable<Dml.ITempPersonIds>(new[] { first.Id, third.Id }.Select(id => new { Id = id }));
    var chosen = await session.EntitySet<Dml.IPerson>()
      .Where(p => staged.Any(t => t.Id == p.Id)).OrderBy(p => p.LastName).ToListAsync();
    CollectionAssert.AreEqual(new[] { first.Id, third.Id }, chosen.Select(p => p.Id).ToList());
    Assert.AreEqual(2, await staged.CountAsync(), "read like any set");

    // ----- the source of a set-based update, inside the save ------------------------------------------------------
    session.EntitySet<Dml.IPerson>().Where(p => staged.Any(t => t.Id == p.Id)).Select(p => new { Rank = 77 })
      .ScheduleUpdate<Dml.IPerson>();
    await session.SaveChangesAsync();
    await using var reader = NewSession();
    var ranks = await reader.EntitySet<Dml.IPerson>().Where(p => p.LastName.StartsWith(tag))
      .OrderBy(p => p.LastName).Select(p => p.Rank).ToListAsync();
    CollectionAssert.AreEqual(new[] { 77, 2, 77 }, ranks.ToList());

    // ----- read-only, and filled once ---------------------------------------------------------------------------
    Assert.ThrowsExactly<InvalidOperationException>(() => session.NewEntity<Dml.ITempPersonIds>());
    Assert.ThrowsExactly<InvalidOperationException>(() => session.CreateTempTable<Dml.ITempPersonIds>([]));

    // ----- created at once, in a session of its own: each session has its own table ------------------------------
    await using var other = NewSession();
    var now = await other.CreateTempTableAsync<Dml.ITempPersonIds>([new { Id = second.Id }]);
    Assert.AreEqual(1, await now.CountAsync());
    Assert.AreEqual(second.Id, (await now.ToListAsync())[0].Id);
  }

  [TestMethod]
  public async Task TempTable_AccumulatesFromQueries() {
    var tag = "acc-" + Guid.NewGuid().ToString("N")[..6];
    await using var session = NewSession();
    for (var rank = 1; rank <= 4; rank++) {
      var person = session.NewEntity<Dml.IPerson>();
      person.Id = Guid.NewGuid();
      person.LastName = $"{tag}-{rank}";
      person.Rank = rank;
    }
    await session.SaveChangesAsync();

    // ----- created empty by the first insert, which selects into it; the second adds to it ------------------------
    var picked = session.CreateTempTable<Dml.ITempPersonIds>();
    var people = session.EntitySet<Dml.IPerson>().Where(p => p.LastName.StartsWith(tag));
    await session.ExecuteInsertAsync<Dml.ITempPersonIds>(people.Where(p => p.Rank == 1).Select(p => new { p.Id }));
    await session.ExecuteInsertAsync<Dml.ITempPersonIds>(people.Where(p => p.Rank >= 3).Select(p => new { p.Id }));
    var ranks = await session.EntitySet<Dml.IPerson>()
      .Where(p => picked.Any(t => t.Id == p.Id)).OrderBy(p => p.Rank).Select(p => p.Rank).ToListAsync();
    CollectionAssert.AreEqual(new[] { 1, 3, 4 }, ranks.ToList());
  }

  [TestMethod]
  public async Task TempTable_OutlivesTheTransactionItWasCreatedIn() {
    // A temp table lives on its connection, which is held open for the session; a transaction begun around its creation
    // and committed must not hand the connection back with the table on it.
    var tag = "trx-" + Guid.NewGuid().ToString("N")[..6];
    await using var session = NewSession();
    var person = session.NewEntity<Dml.IPerson>();
    person.Id = Guid.NewGuid();
    person.LastName = tag;
    person.Rank = 5;
    await session.SaveChangesAsync();

    await session.BeginTransactionAsync();
    var picked = await session.CreateTempTableAsync<Dml.ITempPersonIds>([new { person.Id }]);
    Assert.AreEqual(1, await picked.CountAsync());
    await session.CommitTransactionAsync();

    // The next request would close the connection under NoReuse were the lifetime back to Operation.
    var found = await session.EntitySet<Dml.IPerson>().Where(p => picked.Any(t => t.Id == p.Id)).ToListAsync();
    Assert.AreEqual(1, found.Count);
    Assert.AreEqual(tag, found[0].LastName);
    Assert.AreEqual(1, await picked.CountAsync(), "the table is still there after the commit");
  }
}
