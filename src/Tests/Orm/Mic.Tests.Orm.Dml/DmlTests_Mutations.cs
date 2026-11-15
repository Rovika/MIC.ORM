using Mic.Linq;
using Mic.Linq.ExpressionTree;
using Mic.Orm;
using Mic.Orm.Db;
using Mic.Orm.Logging;
using Mic.Orm.Runtime;
using Mic.Orm.Services;

namespace Mic.Tests.Orm.Db;

// A QUERY RUN AS A STATEMENT: the rows it finds deleted by the server, no record loaded, the count answered.
public abstract partial class DmlTests {
  [TestMethod]
  public async Task DeleteFromQuery_RunsOnTheServer() {
    var tag = "del-" + Guid.NewGuid().ToString("N")[..8];
    await using var writer = NewSession();
    var address = writer.NewEntity<Dml.IAddress>();
    address.Id = Guid.NewGuid();
    address.City = tag;
    Dml.IPerson Person(int rank) {
      var person = writer.NewEntity<Dml.IPerson>();
      person.Id = Guid.NewGuid();
      person.LastName = $"{tag}-{rank}";
      person.Rank = rank;
      person.Address = address;
      return person;
    }
    void Phone(Dml.IPerson person, string number, int priority) {
      var phone = writer.NewEntity<Dml.IPhone>();
      phone.Id = Guid.NewGuid();
      phone.Person = person;
      phone.Number = number;
      phone.Priority = priority;
    }
    var first = Person(1);
    var second = Person(2);
    var third = Person(3);
    Phone(first, tag + "-drop", 5);
    Phone(first, tag + "-drop", 6);
    Phone(second, tag + "-keep", 1);
    Phone(third, tag + "-keep", 9);
    await writer.SaveChangesAsync();

    await using var session = NewSession();
    var phones = session.EntitySet<Dml.IPhone>();
    var people = session.EntitySet<Dml.IPerson>();

    // ----- THE SIMPLE FORM: the table alone, filtered — the query's WHERE is the statement's ---------------------------
    session.LogMessage("delete: the simple form");
    var drop = tag + "-drop";
    var dropped = await phones.Where(ph => ph.Number == drop).ExecuteDeleteAsync();
    Assert.AreEqual(2, dropped, "the rows affected are the answer");
    var sql = session.LastCommands.Single().Sql;
    StringAssert.Contains(sql, "DELETE", "a delete statement");
    Assert.IsFalse(sql.Contains("EXISTS"), "nothing joined, nothing to correlate:\n" + sql);
    Assert.AreEqual(2, (await phones.Where(ph => ph.Number.StartsWith(tag)).ToListAsync()).Count, "the two 'keep' rows are left");

    // ----- THROUGH A REFERENCE: the finding select's keys, `key IN (select)` ------------------------------------------
    session.LogMessage("delete: through a reference");
    var joined = await phones.Where(ph => ph.Person.Address!.City == tag && ph.Priority < 2).ExecuteDeleteAsync();
    Assert.AreEqual(1, joined, "the second person's phone");
    sql = session.LastCommands.Single().Sql;
    StringAssert.Contains(sql, "DELETE", "a delete statement");
    StringAssert.Contains(sql, "Address", "the join is inside the select the key is found in:\n" + sql);
    var left = await phones.Where(ph => ph.Number.StartsWith(tag)).ToListAsync();
    Assert.AreEqual(9, left.Single().Priority, "the third person's phone is the one left");

    // ----- A PAGE OF ORDERED ROWS: the page is a derived table the key is read out of --------------------------------
    session.LogMessage("delete: a page of ordered rows");
    var paged = await people.Where(p => p.Address!.City == tag).OrderBy(p => p.Rank).Take(1).ExecuteDeleteAsync();
    Assert.AreEqual(1, paged);
    sql = session.LastCommands.Single().Sql;
    StringAssert.Contains(sql, "ORDER BY", "the page is taken inside the finding select:\n" + sql);
    var remaining = await people.Where(p => p.Address!.City == tag).OrderBy(p => p.Rank).Select(p => p.Rank).ToListAsync();
    CollectionAssert.AreEqual(new[] { 2, 3 }, remaining.ToList(), "the lowest rank went");

    // ----- REFUSED: rows of a shape have no table; an ordering with no page decides nothing --------------------------
    await Assert.ThrowsExactlyAsync<NotSupportedException>(() => people.Select(p => p.Rank).ExecuteDeleteAsync());
    await Assert.ThrowsExactlyAsync<NotSupportedException>(() => people.OrderBy(p => p.Rank).ExecuteDeleteAsync());

    // ----- BY KEY, THE TARGET NAMED: a projection cut down to the key, whatever else it brought ------------------------
    session.LogMessage("delete by key: a projection with the key among its slots");
    var byShape = await phones.Where(ph => ph.Number.StartsWith(tag)).Select(ph => new { ph.Id, ph.Number })
      .ExecuteDeleteAsync<Dml.IPhone>();
    Assert.AreEqual(1, byShape, "the third person's phone");
    Assert.AreEqual(0, (await phones.Where(ph => ph.Number.StartsWith(tag)).ToListAsync()).Count);

    // ----- BY KEY, OVER ANOTHER ENTITY: parents found through their notes — one value, the key reached through a
    // reference; the notes go with their parent, which is the one way a referenced row can go -------------------------
    var parent = session.NewEntity<Dml.IParent>();
    parent.Id = Guid.NewGuid();
    parent.Code = tag;
    var note = session.NewEntity<Dml.INote>();
    note.Id = Guid.NewGuid();
    note.Parent = parent;
    note.Text = tag;
    await session.SaveChangesAsync();
    var notes = session.EntitySet<Dml.INote>();
    session.LogMessage("delete by key: over another entity");
    Assert.AreEqual(1, await notes.Where(n => n.Text == tag).Select(n => n.Parent.Id).ExecuteDeleteAsync<Dml.IParent>());
    sql = session.LastCommands.Single().Sql;
    StringAssert.Contains(sql, "Note", "the key comes from the notes:\n" + sql);
    Assert.AreEqual(0, (await notes.Where(n => n.Text == tag).ToListAsync()).Count, "the note went with its parent");
    Assert.AreEqual(0, (await session.EntitySet<Dml.IParent>().Where(p => p.Code == tag).ToListAsync()).Count);

    // ----- REFUSED: another entity's rows bring no key; a projection without the key's members -----------------------
    await Assert.ThrowsExactlyAsync<NotSupportedException>(() => notes.ExecuteDeleteAsync<Dml.IParent>());
    await Assert.ThrowsExactlyAsync<NotSupportedException>(() => notes.Select(n => new { n.Text }).ExecuteDeleteAsync<Dml.IParent>());

    // ----- UNDER AN EXTENSION'S FILTER: a tenant deletes its own rows and nobody else's --------------------------------
    var tenantA = Guid.NewGuid();
    var tenantB = Guid.NewGuid();
    IEntitySession For(Guid tenant) {
      var context = NewContext(App);
      context.SetValue(((DmlApp)App).Tenancy.TenantIdKey, tenant);
      return context.OpenSession();
    }
    var sessionA = For(tenantA);
    var jobA = sessionA.NewEntity<Dml.IJob>();
    jobA.Id = Guid.NewGuid();
    jobA.Title = tag;
    await sessionA.SaveChangesAsync();
    var sessionB = For(tenantB);
    var jobB = sessionB.NewEntity<Dml.IJob>();
    jobB.Id = Guid.NewGuid();
    jobB.Title = tag;
    await sessionB.SaveChangesAsync();
    session.LogMessage("delete: under the tenant filter");
    Assert.AreEqual(1, await sessionA.EntitySet<Dml.IJob>().Where(j => j.Title == tag).ExecuteDeleteAsync(), "tenant A's row alone");
    Assert.AreEqual(tag, (await sessionB.EntitySet<Dml.IJob>().Where(j => j.Title == tag).ToListAsync()).Single().Title,
      "tenant B's row is untouched");
    Assert.AreEqual(0, (await sessionA.EntitySet<Dml.IJob>().Where(j => j.Title == tag).ToListAsync()).Count);
  }

  [TestMethod]
  public async Task UpdateFromQuery_RunsOnTheServer() {
    var tag = "upd-" + Guid.NewGuid().ToString("N")[..8];
    await using var writer = NewSession();
    var address = writer.NewEntity<Dml.IAddress>();
    address.Id = Guid.NewGuid();
    address.City = tag;
    Dml.IPerson Person(int rank) {
      var person = writer.NewEntity<Dml.IPerson>();
      person.Id = Guid.NewGuid();
      person.LastName = $"{tag}-{rank}";
      person.Rank = rank;
      person.Address = address;
      return person;
    }
    void Phone(Dml.IPerson person, int priority) {
      var phone = writer.NewEntity<Dml.IPhone>();
      phone.Id = Guid.NewGuid();
      phone.Person = person;
      phone.Number = tag;
      phone.Priority = priority;
    }
    var first = Person(1);
    var second = Person(2);
    Person(3);
    Phone(first, 1);
    Phone(first, 2);
    Phone(second, 3);
    await writer.SaveChangesAsync();

    await using var session = NewSession();
    var people = session.EntitySet<Dml.IPerson>();
    var phones = session.EntitySet<Dml.IPhone>();
    async Task<List<int>> RanksAsync() =>
      (await people.Where(p => p.Address!.City == tag).OrderBy(p => p.LastName).Select(p => p.Rank).ToListAsync()).ToList();

    // ----- THE SIMPLE FORM: the target's own filtered rows, the values expressions over the row ----------------------
    session.LogMessage("update: the simple form");
    var raised = await people.Where(p => p.LastName.StartsWith(tag)).Select(p => new { Rank = p.Rank + 10 })
      .ExecuteUpdateAsync<Dml.IPerson>();
    Assert.AreEqual(3, raised, "the rows affected are the answer");
    var sql = session.LastCommands.Single().Sql;
    StringAssert.Contains(sql, "UPDATE", "an update statement");
    Assert.IsFalse(sql.Contains("SELECT"), "the values are over the row, nothing is read:\n" + sql);
    CollectionAssert.AreEqual(new[] { 11, 12, 13 }, await RanksAsync());

    // ----- OVER THE TARGET'S OWN ROWS WITH A JOIN: the rows found by key, the values read out of the query -------------
    session.LogMessage("update: the values out of a query over the target");
    var doubled = await people.Where(p => p.Address!.City == tag).Select(p => new { p.Id, Rank = p.Rank * 2 })
      .ExecuteUpdateAsync<Dml.IPerson>();
    Assert.AreEqual(3, doubled);
    sql = session.LastCommands.Single().Sql;
    StringAssert.Contains(sql, "SELECT", "the query is a derived table joined on the key:\n" + sql);
    CollectionAssert.AreEqual(new[] { 22, 24, 26 }, await RanksAsync());

    // ----- OVER ANOTHER ENTITY: a grouping's counts written onto the rows they were grouped by ---------------------------
    session.LogMessage("update: a grouping over another entity");
    var counted = await phones.Where(ph => ph.Number == tag).GroupBy(ph => ph.Person.Id)
      .Select(g => new { Id = g.Key, Rank = g.Count() }).ExecuteUpdateAsync<Dml.IPerson>();
    Assert.AreEqual(2, counted, "the two people with phones");
    CollectionAssert.AreEqual(new[] { 2, 1, 26 }, await RanksAsync(), "each person's phone count, the third untouched");

    // ----- REFUSED: a slot naming no member; a bare value; another entity's rows without the key ---------------------
    await Assert.ThrowsExactlyAsync<NotSupportedException>(() => people.Select(p => new { Nope = 1 }).ExecuteUpdateAsync<Dml.IPerson>());
    await Assert.ThrowsExactlyAsync<NotSupportedException>(() => people.Select(p => p.Rank + 1).ExecuteUpdateAsync<Dml.IPerson>());
    await Assert.ThrowsExactlyAsync<NotSupportedException>(() => phones.Select(ph => new { Rank = 1 }).ExecuteUpdateAsync<Dml.IPerson>());

    // ----- UNDER AN EXTENSION'S FILTER: in both forms, a tenant writes its own rows and nobody else's -------------------
    var tenantA = Guid.NewGuid();
    var tenantB = Guid.NewGuid();
    IEntitySession For(Guid tenant) {
      var context = NewContext(App);
      context.SetValue(((DmlApp)App).Tenancy.TenantIdKey, tenant);
      return context.OpenSession();
    }
    var sessionA = For(tenantA);
    var jobA = sessionA.NewEntity<Dml.IJob>();
    jobA.Id = Guid.NewGuid();
    jobA.Title = tag;
    await sessionA.SaveChangesAsync();
    var sessionB = For(tenantB);
    var jobB = sessionB.NewEntity<Dml.IJob>();
    jobB.Id = Guid.NewGuid();
    jobB.Title = tag;
    await sessionB.SaveChangesAsync();
    session.LogMessage("update: under the tenant filter");
    var jobs = sessionA.EntitySet<Dml.IJob>();
    var renamed = tag + "-a";
    Assert.AreEqual(1, await jobs.Where(j => j.Title == tag).Select(j => new { Title = renamed }).ExecuteUpdateAsync<Dml.IJob>(),
      "tenant A's row alone, the simple form");
    var renamedAgain = tag + "-aa";
    Assert.AreEqual(1, await jobs.Where(j => j.Title == renamed).OrderBy(j => j.Id).Take(1).Select(j => new { j.Id, Title = renamedAgain })
      .ExecuteUpdateAsync<Dml.IJob>(), "tenant A's row alone, the values out of a query");
    // READ BACK THROUGH FRESH SESSIONS: the statement ran on the server, and the record `sessionA` tracks was never
    // told — which is the debt `TECH_DEBT.md` records, and what a query through `sessionA` would answer with.
    var readerA = For(tenantA);
    Assert.AreEqual(renamedAgain, (await readerA.EntitySet<Dml.IJob>().Where(j => j.Title.StartsWith(tag)).ToListAsync()).Single().Title);
    var readerB = For(tenantB);
    Assert.AreEqual(tag, (await readerB.EntitySet<Dml.IJob>().Where(j => j.Title.StartsWith(tag)).ToListAsync()).Single().Title,
      "tenant B's row is untouched");
  }

  [TestMethod]
  public async Task InsertFromQuery_RunsOnTheServer() {
    var tag = "ins-" + Guid.NewGuid().ToString("N")[..8];
    var otherTitle = tag + "-2";
    await using var writer = NewSession();
    var ticket = writer.NewEntity<Dml.ITicket>();
    ticket.Title = tag;
    var second = writer.NewEntity<Dml.ITicket>();
    second.Title = otherTitle;
    foreach (var text in new[] { "a", "b" }) {
      var comment = writer.NewEntity<Dml.IComment>();
      comment.Text = text;
      comment.Ticket = ticket;
    }
    await writer.SaveChangesAsync();

    await using var session = NewSession();
    var tickets = session.EntitySet<Dml.ITicket>();
    var comments = session.EntitySet<Dml.IComment>();
    Task<int> CountAsync(string title) => comments.Where(c => c.Ticket.Title == title).CountAsync();

    // ----- ROWS OUT OF A QUERY, a reference written by the row it points at, the identity key made by the server ------
    session.LogMessage("insert: a comment per ticket, the ticket a row of the query");
    var made = await tickets.Where(t => t.Title.StartsWith(tag)).Select(t => new { Text = t.Title, Ticket = t })
      .ExecuteInsertAsync<Dml.IComment>();
    Assert.AreEqual(2, made, "the rows affected are the answer");
    var sql = session.LastCommands.Single().Sql;
    StringAssert.Contains(sql, "INSERT INTO", "an insert statement");
    StringAssert.Contains(sql, "SELECT", "the rows are selected:\n" + sql);
    Assert.AreEqual(3, await CountAsync(tag), "a, b and the new one");
    Assert.AreEqual(1, await CountAsync(otherTitle));

    // ----- COPIED THROUGH A JOIN: the first ticket's comments, onto the second, with a suffix ---------------------------
    session.LogMessage("insert: copies onto another ticket");
    var copied = await comments.Where(c => c.Ticket.Title == tag)
      .SelectMany(c => tickets.Where(t => t.Title == otherTitle), (c, t) => new { Text = c.Text + "!", Ticket = t })
      .ExecuteInsertAsync<Dml.IComment>();
    Assert.AreEqual(3, copied);
    Assert.AreEqual(4, await CountAsync(otherTitle));
    CollectionAssert.AreEquivalent(new[] { "a!", "b!", tag + "!", otherTitle },
      (await comments.Where(c => c.Ticket.Title == otherTitle).Select(c => c.Text).ToListAsync()).ToList());

    // ----- AN UPDATE WRITING A REFERENCE BY ROW: the first ticket's comments moved to the second -------------------------
    session.LogMessage("update: a reference written by a row of the query");
    var moved = await comments.Where(c => c.Ticket.Title == tag)
      .SelectMany(c => tickets.Where(t => t.Title == otherTitle), (c, t) => new { c.Id, Ticket = t })
      .ExecuteUpdateAsync<Dml.IComment>();
    Assert.AreEqual(3, moved);
    Assert.AreEqual(0, await CountAsync(tag));
    Assert.AreEqual(7, await CountAsync(otherTitle));

    // ----- REFUSED: a reference given a value; a slot naming no member; a bare value ----------------------------------
    await Assert.ThrowsExactlyAsync<NotSupportedException>(() =>
      tickets.Select(t => new { Text = t.Title, Ticket = t.Id }).ExecuteInsertAsync<Dml.IComment>());
    await Assert.ThrowsExactlyAsync<NotSupportedException>(() => tickets.Select(t => new { Nope = t.Title }).ExecuteInsertAsync<Dml.IComment>());
    await Assert.ThrowsExactlyAsync<NotSupportedException>(() => tickets.Select(t => t.Title).ExecuteInsertAsync<Dml.IComment>());
  }

  [TestMethod]
  public async Task MutationsByName_RunAsTheTypedOnesDo() {
    var tag = "nam-" + Guid.NewGuid().ToString("N")[..8];
    await using var session = NewSession();
    var model = session.GetLinqModel();
    var person = model.FindEntity("Person")!;
    var phone = model.FindEntity("Phone")!;
    var comment = model.FindEntity("Comment")!;
    var address = session.NewEntity<Dml.IAddress>();
    address.Id = Guid.NewGuid();
    address.City = tag;
    for (var rank = 1; rank <= 3; rank++) {
      var ranked = session.NewEntity<Dml.IPerson>();
      ranked.Id = Guid.NewGuid();
      ranked.LastName = $"{tag}-{rank}";
      ranked.Rank = rank;
      ranked.Address = address;
      foreach (var (number, priority) in new[] { (tag + "-drop", 5), (tag + "-keep", rank) }) {
        var ph = session.NewEntity<Dml.IPhone>();
        ph.Id = Guid.NewGuid();
        ph.Person = ranked;
        ph.Number = number;
        ph.Priority = priority;
      }
    }
    var ticket = session.NewEntity<Dml.ITicket>();
    ticket.Title = tag;
    await session.SaveChangesAsync();
    Task<List<int>> RanksAsync() => session.ExecuteValuesAsync<int>(session.From("Person")
        .Where(row => row.Member("Address").Member("City").Equal(QueryMaker.Constant(tag)))
        .OrderBy(row => row.Member("LastName")).Select(row => [row.Member("Rank")]))
      .ContinueWith(t => t.Result.Select(v => v!).ToList());

    // ----- DELETE, the rows of the source ---------------------------------------------------------------------------------
    session.LogMessage("by name: delete the rows of the source");
    var dropped = await session.ExecuteMutationAsync(session.From("Phone")
      .Where(row => row.Member("Number").Equal(QueryMaker.Constant(tag + "-drop"))).Delete());
    Assert.AreEqual(3, dropped);
    StringAssert.Contains(session.LastCommands.Single().Sql, "DELETE");

    // ----- DELETE BY KEY, the target named, the key a slot of the projection -------------------------------------------
    session.LogMessage("by name: delete by a key the query brings");
    var byKey = await session.ExecuteMutationAsync(session.From("Phone")
      .Where(row => QueryMaker.And(row.Member("Number").Equal(QueryMaker.Constant(tag + "-keep")),
        row.Member("Priority").Less(QueryMaker.Constant(2))))
      .Select(row => [row.Member("Id")]).Delete(phone));
    Assert.AreEqual(1, byKey, "the first person's phone");

    // ----- UPDATE, the projection the statement, a parameter for the value ---------------------------------------------
    session.LogMessage("by name: update with a parameter");
    var raised = await session.ExecuteMutationAsync(session.From("Person")
      .Where(row => row.Member("LastName").In(QueryMaker.Parameter("names", typeof(string[]))))
      .Select(row => [row.Member("Rank").Add(QueryMaker.Constant(10)).As("Rank")]).Update(person),
      [new[] { tag + "-1", tag + "-2" }]);
    Assert.AreEqual(2, raised);
    CollectionAssert.AreEqual(new[] { 11, 12, 3 }, await RanksAsync());

    // ----- INSERT, a reference written by a row of the query ------------------------------------------------------------
    session.LogMessage("by name: insert, the ticket a row of the query");
    var made = await session.ExecuteMutationAsync(session.From("Ticket")
      .Where(row => row.Member("Title").Equal(QueryMaker.Constant(tag)))
      .Select(row => [row.Member("Title").As("Text"), row.As("Ticket")]).Insert(comment));
    Assert.AreEqual(1, made);
    Assert.AreEqual(1, await session.ExecuteScalarAsync<long>(session.From("Comment")
      .Where(row => row.Member("Ticket").Member("Title").Equal(QueryMaker.Constant(tag))).Count()));

    // ----- SCHEDULED into a save, with a record ------------------------------------------------------------------------
    session.LogMessage("by name: scheduled at the end of a save");
    session.ScheduleMutation(session.From("Person")
      .Where(row => row.Member("LastName").Equal(QueryMaker.Parameter("name", typeof(string))))
      .Select(row => [row.Member("Rank").Add(QueryMaker.Constant(100)).As("Rank")]).Update(person),
      CommandSchedule.TransactionEnd, tag + "-3");
    var fourth = session.NewEntity<Dml.IPerson>();
    fourth.Id = Guid.NewGuid();
    fourth.LastName = tag + "-4";
    fourth.Rank = 4;
    fourth.Address = address;
    await session.SaveChangesAsync();
    StringAssert.Contains(session.LastCommands[^1].Sql, "UPDATE", "the scheduled statement ran last");
    CollectionAssert.AreEqual(new[] { 11, 12, 103, 4 }, await RanksAsync());

    // ----- TWO TARGETS OVER ONE QUERY ARE TWO STATEMENTS — the typed path's key takes the type argument -------------------
    session.LogMessage("typed: two targets, two statements");
    var comments = session.EntitySet<Dml.IComment>();
    var none = tag + "-none";
    Assert.AreEqual(0, await comments.Where(c => c.Text == none).Select(c => c.Id).ExecuteDeleteAsync<Dml.IComment>());
    StringAssert.Contains(session.LastCommands.Single().Sql.Split('\n')[0], "Comment");
    Assert.AreEqual(0, await comments.Where(c => c.Text == none).Select(c => c.Id).ExecuteDeleteAsync<Dml.ITicket>());
    StringAssert.Contains(session.LastCommands.Single().Sql.Split('\n')[0], "Ticket", "not the comment's statement again");
  }

  [TestMethod]
  public async Task ScheduledMutations_RunInsideTheSave() {
    var tag = "sch-" + Guid.NewGuid().ToString("N")[..8];
    await using var session = NewSession();
    var address = session.NewEntity<Dml.IAddress>();
    address.Id = Guid.NewGuid();
    address.City = tag;
    Dml.IPerson Person(int rank) {
      var person = session.NewEntity<Dml.IPerson>();
      person.Id = Guid.NewGuid();
      person.LastName = $"{tag}-{rank}";
      person.Rank = rank;
      person.Address = address;
      return person;
    }
    void Phone(Dml.IPerson person, string number) {
      var phone = session.NewEntity<Dml.IPhone>();
      phone.Id = Guid.NewGuid();
      phone.Person = person;
      phone.Number = number;
    }
    var first = Person(1);
    var second = Person(2);
    Phone(first, tag + "-old");
    Phone(second, tag + "-old");
    await session.SaveChangesAsync();
    var phones = session.EntitySet<Dml.IPhone>();
    var people = session.EntitySet<Dml.IPerson>();
    Task<List<string>> NumbersAsync() => phones.Where(ph => ph.Number.StartsWith(tag)).OrderBy(ph => ph.Number)
      .Select(ph => ph.Number).ToListAsync().ContinueWith(t => t.Result.ToList());

    // ----- AT THE START: the rows the save is about to recreate cleared first, in the save's own transaction --------
    session.LogMessage("scheduled: a delete at the start, the records after it");
    phones.Where(ph => ph.Number == tag + "-old").ScheduleDelete(CommandSchedule.TransactionStart);
    Assert.AreEqual(1, session.ScheduledMutations.Count);
    Phone(first, tag + "-new");
    Phone(second, tag + "-new");
    await session.SaveChangesAsync();
    Assert.AreEqual(0, session.ScheduledMutations.Count, "run, and let go");
    StringAssert.Contains(session.LastCommands[0].Sql, "DELETE", "the scheduled statement ran first");
    Assert.AreEqual(2, session.LastCommands[0].RowCount);
    Assert.IsTrue(session.LastCommands.Count >= 2, "the records' statements followed");
    CollectionAssert.AreEqual(new[] { tag + "-new", tag + "-new" }, await NumbersAsync());

    // ----- AT THE END: a recalculation over what the save has just written --------------------------------------------
    session.LogMessage("scheduled: an update at the end, over the records just written");
    Phone(first, tag + "-third");
    phones.Where(ph => ph.Number.StartsWith(tag)).GroupBy(ph => ph.Person.Id).Select(g => new { Id = g.Key, Rank = g.Count() })
      .ScheduleUpdate<Dml.IPerson>();
    await session.SaveChangesAsync();
    StringAssert.Contains(session.LastCommands[^1].Sql, "UPDATE", "the scheduled statement ran last");
    await using var reader = NewSession();
    CollectionAssert.AreEqual(new[] { 2, 1 },
      (await reader.EntitySet<Dml.IPerson>().Where(p => p.Address!.City == tag).OrderBy(p => p.LastName).Select(p => p.Rank).ToListAsync()).ToList(),
      "the first person's two phones counted, the second's one — the third phone was in the same save");

    // ----- SCHEDULED STATEMENTS ALONE are a save too ----------------------------------------------------------------------
    session.LogMessage("scheduled: nothing else to save");
    people.Where(p => p.LastName.StartsWith(tag)).Select(p => new { Rank = p.Rank + 10 }).ScheduleUpdate<Dml.IPerson>();
    await session.SaveChangesAsync();
    Assert.AreEqual(1, session.LastCommands.Count);
    Assert.AreEqual(2, session.LastCommands[0].RowCount);

    // ----- A FAILING STATEMENT FAILS THE SAVE, records and all, and stays scheduled ----------------------------------
    session.LogMessage("scheduled: a delete the server refuses");
    Phone(second, tag + "-never");
    people.Where(p => p.Address!.City == tag).ScheduleDelete();          // their phones still point at them
    await Assert.ThrowsExactlyAsync<DataAccessException>(() => session.SaveChangesAsync());
    Assert.AreEqual(1, session.ScheduledMutations.Count, "a failed save keeps what was pending");
    Assert.AreEqual(0, (await reader.EntitySet<Dml.IPhone>().Where(ph => ph.Number == tag + "-never").ToListAsync()).Count,
      "the record written before the failing statement was rolled back with it");
  }
}

// THE ENTITY CACHE AND A WRITE: a cached entity's block — the writing context's, one block per tenant — is dropped
// after its rows are written, by a save, by a statement run now, by a statement scheduled into a save, and the next
// read fills a fresh one; a write to anything else, and another tenant's write, leave it alone.
public abstract partial class DmlTests {
  [TestMethod]
  public async Task CachedEntity_BlockDroppedWhenRowsAreWritten() {
    var tag = "clr-" + Guid.NewGuid().ToString("N")[..8];
    var cache = SharedDatabase.EntityCache;
    var color = App.Model!.FindEntity(typeof(Dml.IColor))!;
    Assert.IsTrue(EntityCache.IsCached(color));
    Assert.AreEqual(DmlApp.ColorCacheExpiration, color.CacheExpiration, "declared with the cache");
    Assert.AreEqual(TimeSpan.FromSeconds(60), App.DefaultCacheExpiration, "a minute where a declaration says nothing");
    var tenantA = Guid.NewGuid();
    var tenantB = Guid.NewGuid();
    EntitySession For(Guid tenant) {   // the class: the cache is asked for its blocks below
      var context = NewContext(App);
      context.SetValue(((DmlApp)App).Tenancy.TenantIdKey, tenant);
      return new EntitySession(context);
    }
    static async Task<List<string>> NamesAsync(EntitySession session, string prefix) =>
      (await session.EntitySet<Dml.IColor>().Where(c => c.Name.StartsWith(prefix)).OrderBy(c => c.Name).Select(c => c.Name).ToListAsync()).ToList();
    static Dml.IColor Color(EntitySession session, string name) {
      var made = session.NewEntity<Dml.IColor>();
      made.Id = Guid.NewGuid();
      made.Name = name;
      return made;
    }

    // ----- A SAVE from one session, the block filled by a read in another — per tenant ----------------------------------
    var writer = For(tenantA);
    Color(writer, tag + "-red");
    await writer.SaveChangesAsync();
    var writerB = For(tenantB);
    Color(writerB, tag + "-blue");
    await writerB.SaveChangesAsync();
    var reader = For(tenantA);
    var readerB = For(tenantB);
    CollectionAssert.AreEqual(new[] { tag + "-red" }, await NamesAsync(reader, tag));
    CollectionAssert.AreEqual(new[] { tag + "-blue" }, await NamesAsync(readerB, tag), "the other tenant's block, the other tenant's rows");
    var filled = await cache.GetBlockAsync(color, reader);
    var filledB = await cache.GetBlockAsync(color, readerB);
    Assert.AreNotSame(filled, filledB, "one block per tenant");
    var hits = cache.Hits;
    CollectionAssert.AreEqual(new[] { tag + "-red" }, await NamesAsync(reader, tag));
    Assert.AreEqual(hits + 1, cache.Hits, "answered from the block");
    Assert.AreSame(filled, await cache.GetBlockAsync(color, reader), "the same block, nothing written since");

    // ----- A SAVE inserting a row DROPS the tenant's block and no other; the next read sees the row -----------------
    writer.LogMessage("cache: a save drops the tenant's block");
    Color(writer, tag + "-green");
    await writer.SaveChangesAsync();
    CollectionAssert.AreEqual(new[] { tag + "-green", tag + "-red" }, await NamesAsync(reader, tag), "a third party sees the new row");
    var refilled = await cache.GetBlockAsync(color, reader);
    Assert.AreNotSame(filled, refilled, "a fresh block for tenant A");
    Assert.AreSame(filledB, await cache.GetBlockAsync(color, readerB), "tenant B's block is untouched by A's write");

    // ----- A STATEMENT RUN NOW drops it too --------------------------------------------------------------------------
    writer.LogMessage("cache: a statement drops the tenant's block");
    var renamed = tag + "-crimson";
    var oldName = tag + "-red";
    Assert.AreEqual(1, await writer.EntitySet<Dml.IColor>().Where(c => c.Name == oldName).Select(c => new { Name = renamed })
      .ExecuteUpdateAsync<Dml.IColor>());
    CollectionAssert.AreEqual(new[] { tag + "-crimson", tag + "-green" }, await NamesAsync(reader, tag));
    Assert.AreNotSame(refilled, await cache.GetBlockAsync(color, reader));
    Assert.AreSame(filledB, await cache.GetBlockAsync(color, readerB));

    // ----- A STATEMENT SCHEDULED into a save of something else drops it after the save commits -----------------------
    writer.LogMessage("cache: a scheduled statement drops the tenant's block");
    var before = await cache.GetBlockAsync(color, reader);
    writer.EntitySet<Dml.IColor>().Where(c => c.Name == renamed).ScheduleDelete();
    var widget = writer.NewEntity<Dml.IWidget>();
    widget.Id = Guid.NewGuid();
    widget.Name = tag;
    await writer.SaveChangesAsync();
    CollectionAssert.AreEqual(new[] { tag + "-green" }, await NamesAsync(reader, tag));
    Assert.AreNotSame(before, await cache.GetBlockAsync(color, reader));
    Assert.AreSame(filledB, await cache.GetBlockAsync(color, readerB));

    // ----- THE CONTROL: a save of an UNCACHED entity leaves the block as it is ----------------------------------------
    writer.LogMessage("cache: a save of something else leaves the block");
    var kept = await cache.GetBlockAsync(color, reader);
    widget.Name = tag + "-renamed";
    await writer.SaveChangesAsync();
    Assert.AreSame(kept, await cache.GetBlockAsync(color, reader), "nothing of the color's was written");
    CollectionAssert.AreEqual(new[] { tag + "-blue" }, await NamesAsync(readerB, tag), "and B still reads its own, from its own block");

    // ----- EXPIRATION: a write by SOMETHING ELSE — another application over the same tables — is not seen until the
    // block's time is up; then the next read refills it ---------------------------------------------------------------
    writer.LogMessage("cache: another application writes, the block expires");
    var otherApp = NewApp();
    var elsewhere = new EntitySession(NewContext(otherApp));
    elsewhere.Context.SetValue(((DmlApp)otherApp).Tenancy.TenantIdKey, tenantA);
    Color(elsewhere, tag + "-amber");
    await elsewhere.SaveChangesAsync();
    await elsewhere.Context.DisposeAsync();
    CollectionAssert.AreEqual(new[] { tag + "-green" }, await NamesAsync(reader, tag), "this application's block knows nothing of it");
    var clock = (AdjustableTimeService)App.TimeService;
    try {
      clock.TimeShift = DmlApp.ColorCacheExpiration + TimeSpan.FromMinutes(1);
      CollectionAssert.AreEqual(new[] { tag + "-amber", tag + "-green" }, await NamesAsync(reader, tag), "expired, refilled, seen");
      Assert.AreNotSame(kept, await cache.GetBlockAsync(color, reader));
    } finally {
      clock.TimeShift = TimeSpan.Zero;
    }
  }
}

// A SCHEDULED STATEMENT AND ITS LOCALS: the values are the ones the locals had at the Schedule call, whatever is
// assigned to them before the save.
public abstract partial class DmlTests {
  [TestMethod]
  public async Task ScheduledMutation_BindsItsLocalsAsTheyWereWhenScheduled() {
    var tag = "snap-" + Guid.NewGuid().ToString("N")[..6];
    await using var session = NewSession();
    for (var rank = 1; rank <= 4; rank++) {
      var person = session.NewEntity<Dml.IPerson>();
      person.Id = Guid.NewGuid();
      person.LastName = tag;
      person.Rank = rank;
    }
    await session.SaveChangesAsync();
    var people = session.EntitySet<Dml.IPerson>();

    // ----- a local assigned again, and a loop variable read beside a local of the method ---------------------------
    var wanted = 1;
    people.Where(p => p.LastName == tag && p.Rank == wanted).Select(p => new { Rank = 10 })
      .ScheduleUpdate<Dml.IPerson>();
    wanted = 4;
    for (var i = 2; i <= 3; i++)
      people.Where(p => p.LastName == tag && p.Rank == i).Select(p => new { Rank = i * 10 })
        .ScheduleUpdate<Dml.IPerson>();
    await session.SaveChangesAsync();
    await using var reader = NewSession();
    CollectionAssert.AreEqual(new[] { 4, 10, 20, 30 },
      (await reader.EntitySet<Dml.IPerson>().Where(p => p.LastName == tag).OrderBy(p => p.Rank).Select(p => p.Rank)
        .ToListAsync()).ToList(),
      "each statement ran with the value of its call");

    // ----- a captured entity is the same entity: the key the save generates for it is the key bound ----------------
    var ticket = session.NewEntity<Dml.ITicket>();
    ticket.Title = tag;
    session.EntitySet<Dml.ITicket>().Where(t => t.Id == ticket.Id).Select(t => new { Text = "made", Ticket = t })
      .ScheduleInsert<Dml.IComment>();
    await session.SaveChangesAsync();
    Assert.IsTrue(ticket.Id > 0);
    Assert.AreEqual(1, await reader.EntitySet<Dml.IComment>().Where(c => c.Ticket.Title == tag).CountAsync(),
      "the comment found the ticket by the key the insert made");
  }
}
