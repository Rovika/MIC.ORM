using Mic.Linq;
using Mic.Orm;

namespace Mic.Tests.Orm.Db;

// THE PUBLIC SURFACE, AGAINST A DATABASE. This file compiles with `using Mic.Orm;` and `using Mic.Linq;` only, and
// that is its first assertion: everything an application does at run time is reachable from the two namespaces. A
// using added to this file is a regression. The unit suite has the same file without a server (PublicApiTests).
public abstract partial class DmlTests {
  [TestMethod]
  public async Task PublicApi_IsReachableFromTwoNamespaces() {
    var tag = Guid.NewGuid().ToString("N")[..8];
    var context = NewContext(App);
    context.ConnectionReuseMode = DbConnectionReuseMode.KeepOpen;
    var session = context.OpenSession(EntitySessionOptions.None);

    // ----- write: the records, their status, the save, what it ran --------------------------------------------------
    var home = session.NewEntity<Dml.IAddress>();
    home.Id = Guid.NewGuid();
    home.City = tag;
    var person = session.NewEntity<Dml.IPerson>();
    person.Id = Guid.NewGuid();
    person.LastName = tag;
    person.Rank = 3;
    person.Address = home;
    Assert.AreEqual(EntityStatus.New, session.GetChangedRecords()[0].Status);
    var transactionId = session.GetNextTransactionId();
    Assert.AreEqual(transactionId, session.GetNextTransactionId(), "the same until a save writes");
    await session.SaveChangesAsync();
    Assert.AreNotEqual(transactionId, session.GetNextTransactionId(), "a new one after the save");
    var lastCommand = session.GetLastCommand();
    Assert.IsNotNull(lastCommand);
    Assert.IsFalse(string.IsNullOrEmpty(lastCommand.Sql), lastCommand.ToString());   // the row count is the server's: Oracle's batch reports -1

    // ----- read: LINQ with the async operators, Include, the options, the flags, a key --------------------------------
    var reader = context.OpenSession();
    var loaded = await reader.EntitySet<Dml.IPerson>(QueryOptions.SkipEntityCache)
      .Where(p => p.LastName == tag).Include(p => p.Address).FirstAsync();
    Assert.AreEqual(tag, loaded.Address!.City);
    EntityKey key = reader.CreatePrimaryKey<Dml.IPerson>(person.Id);
    Assert.AreSame(loaded, await reader.GetEntityAsync<Dml.IPerson>(key, LoadFlags.None));
    Assert.AreEqual(1, await reader.EntitySet<Dml.IPerson>().Where(p => p.LastName == tag).CountAsync());

    // ----- a query built from names, and direct SQL ------------------------------------------------------------------
    var byName = reader.From("Person").Where(row => row.Member("LastName").Equal(QueryMaker.Constant(tag)));
    Assert.AreEqual(1, (await reader.ExecuteQueryAsync(byName)).Count);
    var table = await reader.RunSqlQueryAsync(
      $"SELECT {Db.Quote("LastName")} FROM {Db.GetTableRef("Person")} WHERE {Db.Quote("LastName")} = @name", ("name", tag));
    Assert.AreEqual(1, table.Rows.Count);

    // ----- the transaction, a mass update, scheduled deletes -----------------------------------------------------------
    var writer = context.OpenSession();
    await writer.BeginTransactionAsync();
    Assert.IsTrue(writer.HasOpenTransaction());
    var ranked = writer.EntitySet<Dml.IPerson>().Where(p => p.LastName == tag).Select(p => new { Rank = 4 });
    Assert.AreEqual(1, await writer.ExecuteUpdateAsync<Dml.IPerson>(ranked));      // Vita's form, on the session
    Assert.AreEqual(1, writer.ExecuteUpdate<Dml.IPerson>(ranked));                 // and synchronous
    Assert.AreEqual(1, await ranked.ExecuteUpdateAsync<Dml.IPerson>());            // the form on the query
    await writer.CommitTransactionAsync();
    writer.ScheduleDelete<Dml.IPerson>(writer.EntitySet<Dml.IPerson>().Where(p => p.LastName == tag));
    writer.EntitySet<Dml.IAddress>().Where(a => a.City == tag).ScheduleDelete(CommandSchedule.TransactionEnd);
    Assert.AreEqual(2, writer.GetScheduledMutations().Count);
    Assert.ThrowsExactly<ArgumentException>(
      () => reader.ScheduleDelete<Dml.IAddress>(writer.EntitySet<Dml.IAddress>()), "another session's query");
    await writer.SaveChangesAsync();
    Assert.AreEqual(0, await reader.EntitySet<Dml.IPerson>(QueryOptions.SkipEntityCache)
      .Where(p => p.LastName == tag).CountAsync());

    // ----- a refused save, and the log mode ----------------------------------------------------------------------------
    var faulty = context.OpenSession();
    var tooLong = faulty.NewEntity<Dml.IAddress>();
    tooLong.Id = Guid.NewGuid();
    tooLong.City = new string('x', 61);
    var faultyTransactionId = faulty.GetNextTransactionId();
    var refused = await Assert.ThrowsExactlyAsync<ClientFaultException>(() => faulty.SaveChangesAsync());
    Assert.AreEqual(ClientFaultCodes.ValueTooLong, refused.Faults[0].Code);
    Assert.AreEqual(faultyTransactionId, faulty.GetNextTransactionId(), "a refused save keeps the id");
    Assert.AreEqual("City", refused.Faults[0].MemberName);
    context.LogMode = LogMode.None;
    await context.DisposeAsync();
  }
}
