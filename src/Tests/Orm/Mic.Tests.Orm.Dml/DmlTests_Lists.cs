using Mic.Linq;
using Mic.Linq.Translation;
using Mic.Orm;
using Mic.Orm.Db.Provider;
using Mic.Orm.Logging;
using Mic.Orm.Model;
using Mic.Orm.Runtime;

namespace Mic.Tests.Orm.Db;

// LIST MEMBERS — the other half of a relationship, seen from the parent's end. `person.Phones` is a query the engine
// runs when the list is first looked into, and never before.
public abstract partial class DmlTests {
  [TestMethod]
  public void QueryableMember_TakesNoStorageSlot() {
    // `IPerson.PhoneQuery` is declared SECOND on purpose: every member after it then has a position and a slot that
    // differ, so every save, read and stub of a Person in this suite runs with the two apart. This says they ARE.
    var person = App.Model!.FindEntity(typeof(Dml.IPerson))!;
    var query = person.GetMember("PhoneQuery");
    Assert.IsTrue(query.IsQueryable);
    Assert.AreEqual(MemberKind.List, query.Kind, "the same kind as a list, so the query engine needs telling nothing");
    Assert.AreEqual("Phone", query.ListInfo!.TargetEntity.Name);
    Assert.AreEqual("Person", query.ListInfo.InverseMember!.Name, "resolved by the same rules a list's is");
    Assert.AreEqual(-1, query.DataIndex);
    Assert.AreEqual(1, query.Index);
    Assert.AreEqual(person.Members.Count - 1, person.DataCount, "one member, no slot");

    var lastName = person.GetMember("LastName");
    Assert.AreEqual(lastName.Index - 1, lastName.DataIndex, "what comes after it sits one slot earlier than its position");
    Assert.AreSame(lastName, person.MembersBySlot[lastName.DataIndex], "and a mask's bit finds its member by SLOT");
  }

  [TestMethod]
  public async Task ListMember_LoadsOnFirstTouchInOrder() {
    await using var writer = NewSession();
    var home = writer.NewEntity<Dml.IAddress>();
    home.Id = Guid.NewGuid();
    home.City = "Tallinn";

    var person = writer.NewEntity<Dml.IPerson>();
    person.Id = Guid.NewGuid();
    person.LastName = "Okafor";
    person.Address = home;

    // ----- a NEW record's list is empty and loaded, and adding to it points the child back ----------------------------
    Assert.AreEqual(0, person.Phones.Count, "a new entity's list has nothing to fetch");
    foreach (var (number, priority) in new[] { ("555-0100", 1), ("555-0101", 3), ("555-0102", 2), ("555-0099", 3) }) {
      var phone = writer.NewEntity<Dml.IPhone>();
      phone.Id = Guid.NewGuid();
      phone.Number = number;
      phone.Priority = priority;
      person.Phones.Add(phone);
      Assert.AreSame(person, phone.Person, "adding to the list pointed the phone at its owner");
    }
    Assert.AreEqual(4, person.Phones.Count);

    var loner = writer.NewEntity<Dml.IPerson>();
    loner.Id = Guid.NewGuid();
    loner.LastName = "Vasquez";
    loner.Address = home;
    await writer.SaveChangesAsync();

    // ----- read back: the member costs nothing, the contents cost one query --------------------------------------------
    await using var reader = NewSession();
    var read = await reader.GetEntityAsync<Dml.IPerson>(person.Id);
    var phones = read.Phones;
    Assert.IsFalse(((IMemberEntityList)phones).IsLoaded, "reading the member fetched nothing");

    reader.LogMessage("the list LOADS on first touch, in its declared order: Priority descending, then Number");
    CollectionAssert.AreEqual(new[] { "555-0099", "555-0101", "555-0102", "555-0100" },
      phones.Select(p => p.Number).ToList());
    Assert.IsTrue(((IMemberEntityList)phones).IsLoaded);

    reader.LogMessage("the same load query BY NAME, with the caller's own order over the declared one: the caller's wins");
    var readRecord = EntityHelper.GetRecord(read);
    var phonesMember = readRecord.Entity.GetMember("Phones");
    var byNumber = await reader.ExecuteQueryAsync(
      MemberQueryHelper.BuildListLoadQuery(phonesMember).OrderBy(p => p.Member("Number")), readRecord);
    var numberMember = phonesMember.ListInfo!.TargetEntity.GetMember("Number");
    CollectionAssert.AreEqual(new[] { "555-0099", "555-0100", "555-0101", "555-0102" },
      byNumber.Select(r => (string?)r.GetValue(numberMember)).ToList());
    var orderBy = reader.LastCommands.Single().Sql.Split("ORDER BY")[^1];
    Assert.IsFalse(orderBy.Contains("Priority"), "the declared order is gone from the statement: " + orderBy);
    Assert.IsFalse(((IMemberEntityList)phones).IsModified, "being loaded is not a change");
    Assert.AreSame(read, phones[0].Person, "a loaded child points at the tracked owner, not at a second copy");
    Assert.AreSame(phones, read.Phones, "the same list object every time");

    reader.LogMessage("a list with nothing in it is empty, not null");
    var none = await reader.GetEntityAsync<Dml.IPerson>(loner.Id);
    Assert.AreEqual(0, none.Phones.Count);

    // ----- no declared order: the target's PRIMARY KEY orders it --------------------------------------------------------
    reader.LogMessage("a list with no declared order loads in primary-key order");
    // THE STATEMENT is what is asserted, not the sequence: how two Guids sort is the SERVER's business — SQL Server
    // compares a `uniqueidentifier` by its last six bytes first — so an expectation sorted in .NET agrees with it
    // half the time, which is how this passed five vendors once and failed the next run.
    var residents = (await reader.GetEntityAsync<Dml.IAddress>(home.Id)).Residents;
    CollectionAssert.AreEquivalent(new[] { person.Id, loner.Id }, residents.Select(r => r.Id).ToList());
    var ordered = reader.LastCommands.Single().Sql.TrimEnd();
    StringAssert.Contains(ordered, "\n  ORDER BY ");
    Assert.IsTrue(ordered.EndsWith("Id\"") || ordered.EndsWith("Id]") || ordered.EndsWith("Id`"), ordered);
  }

  [TestMethod]
  public async Task ManyToManyList_LoadsThroughTheLinkInOneStatement() {
    // The links are written by hand: carrying a list's changes into the save is a later step.
    await using var writer = NewSession();
    var owner = writer.NewEntity<Dml.IOwner>();
    owner.Id = Guid.NewGuid();
    owner.Name = "Halvorsen";
    var position = 0;
    foreach (var name in new[] { "red", "blue", "green" }) {
      var tag = writer.NewEntity<Dml.ITag>();
      tag.Id = Guid.NewGuid();
      tag.Name = name;
      var link = writer.NewEntity<Dml.IOwnerTag>();
      link.Id = Guid.NewGuid();
      link.Owner = owner;
      link.Tag = tag;
      link.Position = ++position;
    }
    var untagged = writer.NewEntity<Dml.IOwner>();
    untagged.Id = Guid.NewGuid();
    untagged.Name = "Nobody";
    await writer.SaveChangesAsync();

    await using var reader = NewSession();
    var read = await reader.GetEntityAsync<Dml.IOwner>(owner.Id);
    var tags = (MemberEntityList<Dml.ITag>)read.Tags;
    Assert.IsFalse(tags.IsLoaded, "reading the member fetched nothing");

    reader.LogMessage("the list LOADS through the link, in the order PERSISTED in the link: Position");
    CollectionAssert.AreEqual(new[] { "red", "blue", "green" }, tags.Select(t => t.Name).ToList());
    Assert.IsTrue(tags.IsLoaded);
    Assert.AreSame(tags[0], await reader.GetEntityAsync<Dml.ITag>(tags[0].Id), "the targets are tracked");

    // The LINK behind each target is kept, for the save to diff against.
    Assert.AreEqual(3, tags.Links!.Count);
    foreach (var tag in tags) {
      var link = tags.Links[((EntityBase)tag).Record];
      Assert.AreEqual(EntityStatus.Loaded, link.Status, "the link came back with the target, in the same row");
      Assert.AreSame(read, ((Dml.IOwnerTag)link.EntityInstance).Owner);
      Assert.AreSame(tag, ((Dml.IOwnerTag)link.EntityInstance).Tag);
    }

    reader.LogMessage("an owner with no links has an empty list");
    var none = await reader.GetEntityAsync<Dml.IOwner>(untagged.Id);
    Assert.AreEqual(0, none.Tags.Count);
    Assert.AreEqual(0, ((MemberEntityList<Dml.ITag>)none.Tags).Links!.Count);
  }

  [TestMethod]
  public async Task ListChanges_AreCarriedIntoTheSave() {
    // ----- many-to-many: the links are the ORM's to make -------------------------------------------------------------
    await using var writer = NewSession();
    var owner = writer.NewEntity<Dml.IOwner>();
    owner.Id = Guid.NewGuid();
    owner.Name = "Ibarra";
    Dml.ITag NewTag(EntitySession session, string name) {
      var tag = session.NewEntity<Dml.ITag>();
      tag.Id = Guid.NewGuid();
      tag.Name = name;
      return tag;
    }
    var red = NewTag(writer, "red");
    var blue = NewTag(writer, "blue");
    var green = NewTag(writer, "green");
    owner.Tags.Add(red);
    owner.Tags.Add(blue);
    owner.Tags.Add(green);
    owner.Tags.Remove(green);
    owner.Tags.Add(green);           // out and back in: one link, not two
    Assert.IsTrue(((IMemberEntityList)owner.Tags).IsModified);
    writer.LogMessage("a NEW owner's list: one link row per target, positions 1..n, in ONE save");
    await writer.SaveChangesAsync();

    var written = (MemberEntityList<Dml.ITag>)owner.Tags;
    Assert.IsFalse(written.IsModified, "saved is not modified");
    Assert.AreEqual(3, written.Links!.Count);
    CollectionAssert.AreEqual(new[] { 1, 2, 3 },
      written.Select(t => ((Dml.IOwnerTag)written.Links[((EntityBase)t).Record].EntityInstance).Position).ToList());

    // ----- a loaded list, changed: a link deleted, a link made, positions renumbered ---------------------------------
    await using var editor = NewSession();
    var edited = (MemberEntityList<Dml.ITag>)(await editor.GetEntityAsync<Dml.IOwner>(owner.Id)).Tags;
    CollectionAssert.AreEqual(new[] { "red", "blue", "green" }, edited.Select(t => t.Name).ToList());
    var blueLinkId = ((Dml.IOwnerTag)edited.Links![((EntityBase)edited[1]).Record].EntityInstance).Id;
    edited.RemoveAt(1);                                          // blue goes
    edited.Insert(0, NewTag(editor, "white"));                   // white comes first
    editor.LogMessage("the list is DIFFED against its links: one DELETE, one INSERT, two positions moved");
    await editor.SaveChangesAsync();
    Assert.AreEqual(3, edited.Links.Count, "the lookup follows the save");

    await using var reader = NewSession();
    var read = (MemberEntityList<Dml.ITag>)(await reader.GetEntityAsync<Dml.IOwner>(owner.Id)).Tags;
    CollectionAssert.AreEqual(new[] { "white", "red", "green" }, read.Select(t => t.Name).ToList());
    CollectionAssert.AreEqual(new[] { 1, 2, 3 },
      read.Select(t => ((Dml.IOwnerTag)read.Links![((EntityBase)t).Record].EntityInstance).Position).ToList());
    Assert.IsNull(await reader.GetEntityAsync<Dml.IOwnerTag>(blueLinkId), "the link whose target left is gone");

    // ----- one-to-many: removing a child clears its reference back where the model allows a null ----------------------
    var home = writer.NewEntity<Dml.IAddress>();
    home.Id = Guid.NewGuid();
    home.City = "Bergen";
    var first = writer.NewEntity<Dml.IPerson>();
    first.Id = Guid.NewGuid();
    first.LastName = "Aas";
    var second = writer.NewEntity<Dml.IPerson>();
    second.Id = Guid.NewGuid();
    second.LastName = "Berg";
    home.Residents.Add(first);
    home.Residents.Add(second);
    await writer.SaveChangesAsync();

    var moving = await editor.GetEntityAsync<Dml.IPerson>(first.Id);
    var residents = (await editor.GetEntityAsync<Dml.IAddress>(home.Id)).Residents;
    Assert.AreEqual(2, residents.Count);
    residents.Remove(moving);
    Assert.IsNull(moving.Address, "`Address` is nullable, so leaving the list cleared it");
    editor.LogMessage("a child removed from a one-to-many list is UPDATED, not deleted");
    await editor.SaveChangesAsync();

    var stayed = (await reader.GetEntityAsync<Dml.IAddress>(home.Id)).Residents;
    CollectionAssert.AreEqual(new[] { second.Id }, stayed.Select(r => r.Id).ToList());
    Assert.IsNull((await reader.GetEntityAsync<Dml.IPerson>(first.Id)).Address);

    // A REQUIRED reference back is left alone: the phone still belongs to the person, only the in-memory list forgot it.
    var phone = writer.NewEntity<Dml.IPhone>();
    phone.Id = Guid.NewGuid();
    phone.Number = "555-0200";
    var person = await writer.GetEntityAsync<Dml.IPerson>(first.Id);
    person.Phones.Add(phone);
    person.Phones.Remove(phone);
    Assert.AreSame(person, phone.Person, "`Person` is required, so removal cannot clear it");
  }

  [TestMethod]
  public async Task SmartLoading_FillsSiblingsInOneStatement() {
    // Three people, each at their own address, each with phones — the shape N+1 is made of.
    await using var writer = NewSession();
    var ids = new List<Guid>();
    foreach (var (name, phoneCount) in new[] { ("Lindqvist", 2), ("Mbeki", 1), ("Novak", 0) }) {
      var address = writer.NewEntity<Dml.IAddress>();
      address.Id = Guid.NewGuid();
      address.City = name + " St";
      var person = writer.NewEntity<Dml.IPerson>();
      person.Id = Guid.NewGuid();
      person.LastName = name;
      person.Address = address;
      ids.Add(person.Id);
      for (var i = 0; i < phoneCount; i++) {
        var phone = writer.NewEntity<Dml.IPhone>();
        phone.Id = Guid.NewGuid();
        phone.Number = $"555-03{i}{ids.Count}";
        person.Phones.Add(phone);
      }
    }
    await writer.SaveChangesAsync();

    await using var reader = NewSession();
    var people = (await reader.ExecuteQueryAsync(
        reader.From("Person").Where(row => row.Member("Id").In(QueryMaker.Parameter("ids", typeof(Guid[]))))
          .OrderBy(row => row.Member("LastName")),
        [ids.ToArray()]))
      .Select(r => (Dml.IPerson)r.EntityInstance).ToList();
    Assert.AreEqual(3, people.Count);

    // ----- the framework's own loads ask the QUERY CACHE nothing: their statements are kept on the table ----------
    var queryCache = App.DataAccess.FindDatabase()!.QueryCache;
    var (hits, misses, count) = (queryCache.Hits, queryCache.Misses, queryCache.Count);
    // ----- stubs: touching one address loads all three ---------------------------------------------------------------
    var stubs = people.Select(p => ((EntityBase)p.Address!).Record).ToList();
    Assert.IsTrue(stubs.All(s => s.Status == EntityStatus.Stub), "reading the references fetched nothing");
    reader.LogMessage("SMART: one address read, three loaded");
    Assert.AreEqual("Lindqvist St", people[0].Address!.City);
    // ONE statement, and the keys are ONE bound value — the rows of a parameter — on every vendor: no key list is
    // written into the text.
    var expanded = reader.LastCommands.Single();
    Assert.IsFalse(expanded.Sql.Contains(" IN ("), "no key list written in: " + expanded.Sql);
    Assert.IsTrue(stubs.All(s => s.Status == EntityStatus.Loaded), "the siblings were filled in the same statement");
    Assert.AreEqual("Novak St", people[2].Address!.City);
    Assert.AreSame(expanded, reader.LastCommands.Single(), "and reading them costs nothing more");

    // ----- lists: touching one loads all three, including the one that is empty --------------------------------------
    reader.LogMessage("SMART: one list touched, three loaded — one of them empty");
    Assert.AreEqual(2, people[0].Phones.Count);
    var loaded = reader.LastCommands.Single();
    Assert.IsTrue(people.All(p => ((IMemberEntityList)p.Phones).IsLoaded), "every sibling's list is loaded");
    Assert.AreEqual(1, people[1].Phones.Count);
    Assert.AreEqual(0, people[2].Phones.Count, "no rows is an answer, not a reason to ask again");
    Assert.AreSame(loaded, reader.LastCommands.Single());

    // TWO STATEMENTS, TWO LOOKUPS — the stubs by their keys, the lists by their owners — each under a fixed key computed
    // from the model: found where the shape was met before, translated once where not, and no tree built or hashed
    // either way.
    Assert.AreEqual(hits + misses + 2, queryCache.Hits + queryCache.Misses,
      "stubs and lists, one owner or many: one lookup each, under a fixed key");
    Assert.IsTrue(queryCache.Count <= count + 2, "at most one new entry per shape");

    // ----- a QUERYABLE property: one ask answers every sibling, FOR AS LONG AS THE OWNER LIVES ----------------------
    reader.LogMessage("SMART: a queryable property asked of one owner is answered for all three");
    Assert.AreEqual(2, (await people[0].PhoneQuery.OrderBy(p => p.Number).ToListAsync()).Count);
    var asked = reader.LastCommands.Single();
    Assert.AreEqual(0, (await people[2].PhoneQuery.OrderBy(p => p.Number).ToListAsync()).Count, "no rows is an answer too");
    Assert.AreSame(asked, reader.LastCommands.Single(), "kept by the session, the property having no slot to keep it in");

    // A SAVE LEAVES THE ANSWERS ALONE. Reading owners with their rows and saving after each one is an ordinary unit of
    // work, and answers dropped on every save would make each owner's ask run again for all of them.
    var added = reader.NewEntity<Dml.IPhone>();
    added.Id = Guid.NewGuid();
    added.Number = "555-0399";
    added.Person = people[2];
    await reader.SaveChangesAsync();
    var saved = reader.LastCommands;
    Assert.AreEqual(1, (await people[1].PhoneQuery.OrderBy(p => p.Number).ToListAsync()).Count);
    Assert.AreSame(saved, reader.LastCommands, "the sibling was answered from what was kept, after the save as before it");
    Assert.AreEqual(0, (await people[2].PhoneQuery.OrderBy(p => p.Number).ToListAsync()).Count,
      "as of when it was asked — what a loaded list is too");

    // The database is asked again by whoever switches the keeping off.
    reader.Options = EntitySessionOptions.DisableSmartLoad;
    Assert.AreEqual(1, (await people[2].PhoneQuery.OrderBy(p => p.Number).ToListAsync()).Count);
    reader.Options = EntitySessionOptions.None;

    // ----- switched off: one row at a time, as before ------------------------------------------------------------------
    await using var plain = NewSession();
    plain.Options = EntitySessionOptions.DisableSmartLoad;
    var two = (await plain.ExecuteQueryAsync(
        plain.From("Person").Where(row => row.Member("Id").In(QueryMaker.Parameter("ids", typeof(Guid[])))),
        [ids.Take(2).ToArray()]))
      .Select(r => (Dml.IPerson)r.EntityInstance).ToList();
    _ = two[0].Address!.City;
    Assert.AreEqual(EntityStatus.Stub, ((EntityBase)two[1].Address!).Record.Status, "the sibling was left alone");
  }
  [TestMethod]
  public async Task QueryablePropertyAnswer_LivesWithItsOwner() {
    // THE CLAIM: the kept rows are held strongly, a row whose reference back has been read holds its owner strongly,
    // and the owner is still collected — because the session keeps the rows in a table whose values do not count
    // towards their key's reachability. A dictionary here would keep every such owner for the session's life.
    //
    // IT ALSO GUARDS THE QUERY CACHE. A cached statement's parameter getters once captured the expression they were
    // composed from, which holds the FIRST caller's captured object — here the owner itself — for the application's
    // life. With smart loading off the same owners must go too.
    await using var writer = NewSession();
    var ids = new List<Guid>();
    foreach (var name in new[] { "Quist", "Rahal" }) {
      var person = writer.NewEntity<Dml.IPerson>();
      person.Id = Guid.NewGuid();
      person.LastName = name;
      ids.Add(person.Id);
      var phone = writer.NewEntity<Dml.IPhone>();
      phone.Id = Guid.NewGuid();
      phone.Number = "555-04" + ids.Count;
      phone.Person = person;
    }
    await writer.SaveChangesAsync();

    await using var session = NewSession();
    // ON ANOTHER THREAD AND WAITED FOR, not awaited: an awaited call leaves its state machine — and the locals hoisted
    // into it — reachable from this method's own, and the test would be measuring that.
    var owners = Task.Run(() => AskAndLetGoAsync(session, ids)).GetAwaiter().GetResult();
    for (var i = 0; i < 3 && owners.Any(o => o.TryGetTarget(out _)); i++) {
      GC.Collect();
      GC.WaitForPendingFinalizers();
    }
    Assert.IsFalse(owners.Any(o => o.TryGetTarget(out _)), "the owners went, and what they were answered with went too");

    await using var plain = NewSession();
    plain.Options = EntitySessionOptions.DisableSmartLoad;
    var unkept = Task.Run(() => AskAndLetGoAsync(plain, ids)).GetAwaiter().GetResult();
    for (var i = 0; i < 3 && unkept.Any(o => o.TryGetTarget(out _)); i++) {
      GC.Collect();
      GC.WaitForPendingFinalizers();
    }
    Assert.IsFalse(unkept.Any(o => o.TryGetTarget(out _)), "and a statement kept in the query cache holds no caller's objects");

    // In a method of its own, so that no local of the test keeps anything alive past it.
    static async Task<List<WeakReference<EntityRecord>>> AskAndLetGoAsync(EntitySession session, List<Guid> ids) {
      var people = (await session.ExecuteQueryAsync(
          session.From("Person").Where(row => row.Member("Id").In(QueryMaker.Parameter("ids", typeof(Guid[])))),
          [ids.ToArray()]))
        .Select(r => (Dml.IPerson)r.EntityInstance).ToList();
      var phones = await people[0].PhoneQuery.OrderBy(p => p.Number).ToListAsync();
      Assert.AreSame(people[0], phones[0].Person, "the row points back at its owner — the chain that must not hold it");
      foreach (var phone in await people[1].PhoneQuery.OrderBy(p => p.Number).ToListAsync())
        Assert.AreSame(people[1], phone.Person);
      return [.. people.Select(p => new WeakReference<EntityRecord>(((EntityBase)p).Record))];
    }
  }
  [TestMethod]
  public async Task SynchronousMembers_DoNotDeadlock() {
    // A DESKTOP UI THREAD, in miniature: a synchronization context whose thread is the one that is blocked, so a
    // continuation posted to it never runs. Every synchronous member blocks on async work underneath, and each await
    // in there would capture this context and wait for ever on the thread waiting for it — were the context not taken
    // away while the work starts. `Posts` staying zero says nothing even tried.
    var stuck = new StuckContext();
    Exception? failure = null;
    await using var session = NewSession();
    await using var reader = NewSession();
    var thread = new Thread(() => {
      SynchronizationContext.SetSynchronizationContext(stuck);
      try {
        var address = session.NewEntity<Dml.IAddress>();
        address.Id = Guid.NewGuid();
        address.City = "Stuckholm";
        var person = session.NewEntity<Dml.IPerson>();
        person.Id = Guid.NewGuid();
        person.LastName = "Blocker";
        person.Address = address;
        var phone = session.NewEntity<Dml.IPhone>();
        phone.Id = Guid.NewGuid();
        phone.Number = "555-0500";
        phone.Person = person;
        session.SaveChanges();                                                        // a save

        var read = reader.GetEntity<Dml.IPerson>(person.Id);                          // a read by key
        Assert.AreEqual("Stuckholm", read.Address!.City);                             // a stub expanded from a getter
        Assert.AreEqual(1, read.Phones.Count);                                        // a list loaded from a getter
        Assert.IsFalse(reader.CanDeleteEntity(read, out var blocking));               // the delete check
        Assert.AreEqual(1, blocking.Length);
        Assert.AreSame(stuck, SynchronizationContext.Current, "and the caller's context is where it was left");
      } catch (Exception ex) {
        failure = ex;
      }
    }) { IsBackground = true };
    thread.Start();
    Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(30)), "a synchronous member deadlocked on its own continuation");
    Assert.IsNull(failure, failure?.ToString());
    Assert.AreEqual(0, stuck.Posts, "nothing was posted back to the blocked thread");
  }

  [TestMethod]
  public void SynchronousMembers_RunWithoutWaiting() {
    // A synchronous member runs its session under RunMode.Sync, where every database call is the driver's
    // synchronous one: the work is finished when it returns its task, so no call waits for one. A cached entity is
    // read through a second session the cache opens, which runs as the first one does.
    var tag = "sync-" + Guid.NewGuid().ToString("N")[..6];
    var tenant = Guid.NewGuid();
    EntitySession OpenTenantSession() {
      var context = NewContext(App);
      context.SetValue(((DmlApp)App).Tenancy.TenantIdKey, tenant);
      return new EntitySession(context);
    }
    Guid personId, colorId;
    using (var writer = OpenTenantSession()) {                                        // a synchronous dispose
      var address = writer.NewEntity<Dml.IAddress>();
      address.Id = Guid.NewGuid();
      address.City = tag;
      var person = writer.NewEntity<Dml.IPerson>();
      person.Id = personId = Guid.NewGuid();
      person.LastName = tag;
      person.Address = address;
      var phone = writer.NewEntity<Dml.IPhone>();
      phone.Id = Guid.NewGuid();
      phone.Number = tag;
      phone.Person = person;
      var color = writer.NewEntity<Dml.IColor>();
      color.Id = colorId = Guid.NewGuid();
      color.Name = tag;
      writer.SaveChanges();                                                           // a save
      Assert.AreEqual(RunMode.Async, writer.RunMode, "the mode lasts as long as the member");
      Assert.AreEqual(0, writer.BlockedSyncCallCount);
    }

    using var reader = NewSession();
    var read = reader.GetEntity<Dml.IPerson>(personId);                               // a read by key
    Assert.AreEqual(tag, read.Address!.City);                                         // a stub expanded from a getter
    Assert.AreEqual(1, read.Phones.Count);                                            // a list loaded from a getter
    Assert.IsFalse(reader.CanDeleteEntity(read, out _));                              // the delete check
    Assert.IsTrue(reader.GetEntities<Dml.IPerson>(p => p.LastName, take: 3).Count > 0);   // a page
    var tagged = reader.EntitySet<Dml.IPerson>().Where(p => p.LastName == tag);
    Assert.AreEqual(1, reader.ExecuteUpdate<Dml.IPerson>(tagged.Select(p => new { Rank = 4 })));   // a mass update
    // A locked read begins a transaction, and the save commits it.
    var locked = reader.GetEntity<Dml.IPerson>(personId, LoadFlags.WriteLock);
    locked.Rank = 5;
    reader.SaveChanges();
    Assert.IsFalse(reader.HasOpenTransaction);
    if (Db.Provider.Supports(DbFeatures.Sequences))
      Assert.IsTrue(reader.GetSequenceNextValue("mic_receipt_numbers") > 0);          // a sequence
    Assert.AreEqual(1, reader.ExecuteDelete<Dml.IPhone>(
      reader.EntitySet<Dml.IPhone>().Where(ph => ph.Number == tag)));                 // a mass delete
    Assert.AreEqual(0, reader.BlockedSyncCallCount, "every call was finished when it returned");

    using var cacheReader = OpenTenantSession();
    // A cached block is filled on the way.
    Assert.AreEqual(tag, cacheReader.GetEntity<Dml.IColor>(colorId).Name);
    Assert.AreEqual(0, cacheReader.BlockedSyncCallCount, "the cache's own session ran synchronously too");
  }

  [TestMethod]
  public async Task SmartLoading_ServesACompositeKey() {
    // Three routes keyed by (Region, Code), with stops — an owner two columns identify, which no IN over one column
    // could name. The same three loads as for a Guid key: lists, stubs, a queryable property.
    await using var writer = NewSession();
    foreach (var (region, code, stopCount) in new[] { ("N", 1, 2), ("N", 2, 1), ("S", 1, 0) }) {
      var route = writer.NewEntity<Dml.IRoute>();
      route.Region = region;
      route.Code = code;
      route.Name = $"{region}{code}";
      for (var i = 0; i < stopCount; i++) {
        var stop = writer.NewEntity<Dml.IStop>();
        stop.Id = Guid.NewGuid();
        stop.Position = i;
        stop.Name = $"{region}{code}-{i}";
        route.Stops.Add(stop);
      }
    }
    await writer.SaveChangesAsync();

    await using var reader = NewSession();
    var routes = (await reader.ExecuteQueryAsync(reader.From("Route").OrderBy(row => row.Member("Name"))))
      .Select(r => (Dml.IRoute)r.EntityInstance).ToList();
    Assert.AreEqual(3, routes.Count);

    // ----- lists: one touched, three loaded — the owners bound as ONE value of two columns ---------------------------
    reader.LogMessage("SMART, composite: one list touched, three loaded");
    Assert.AreEqual(2, routes[0].Stops.Count);
    var loaded = reader.LastCommands.Single();
    Assert.IsTrue(routes.All(r => ((IMemberEntityList)r.Stops).IsLoaded), "every sibling's list is loaded");
    Assert.AreEqual(1, routes[1].Stops.Count);
    Assert.AreEqual(0, routes[2].Stops.Count, "no rows is an answer");
    Assert.AreSame(loaded, reader.LastCommands.Single());
    Assert.IsFalse(loaded.Sql.Contains(" IN ("), "no key list written in: " + loaded.Sql);
    Console.WriteLine(loaded.Sql);
    Assert.AreEqual("N1-1", routes[0].Stops[1].Name, "in the declared order");

    // ----- stubs: the stops' routes, from a fresh session, expanded by their two-column keys at once ------------------
    await using var freshSession = NewSession();
    var stops = (await freshSession.ExecuteQueryAsync(freshSession.From("Stop").OrderBy(row => row.Member("Name"))))
      .Select(r => (Dml.IStop)r.EntityInstance).ToList();
    Assert.AreEqual(3, stops.Count);
    var stubs = stops.Select(s => ((EntityBase)s.Route).Record).Distinct().ToList();
    Assert.AreEqual(2, stubs.Count, "two routes referenced, one of them twice");
    Assert.IsTrue(stubs.All(s => s.Status == EntityStatus.Stub), "reading the references fetched nothing");
    freshSession.LogMessage("SMART, composite: one route read, both loaded");
    Assert.AreEqual("N1", stops[0].Route.Name);
    var expanded = freshSession.LastCommands.Single();
    Assert.IsTrue(stubs.All(s => s.Status == EntityStatus.Loaded), "the sibling stub was filled in the same statement");
    Assert.AreEqual("N2", stops[2].Route.Name);
    Assert.AreSame(expanded, freshSession.LastCommands.Single(), "and reading it costs nothing more");
    Assert.IsFalse(expanded.Sql.Contains(" IN ("), "no key list written in: " + expanded.Sql);

    // ----- a queryable property: a value per owner grouped by both key columns, and a page per owner ------------------
    reader.LogMessage("SMART, composite: a count per owner");
    Assert.AreEqual(2, await routes[0].StopQuery.CountAsync());
    var counted = reader.LastCommands.Single();
    Assert.AreEqual(0, await routes[2].StopQuery.CountAsync(), "no group is a count of nothing");
    Assert.AreSame(counted, reader.LastCommands.Single(), "answered from the result kept per owner");

    reader.LogMessage("SMART, composite: the first stop per owner");
    var first = await routes[0].StopQuery.OrderBy(s => s.Position).FirstOrDefaultAsync();
    Assert.AreEqual("N1-0", first!.Name);
    var paged = reader.LastCommands.Single();
    Assert.IsNull(await routes[2].StopQuery.OrderBy(s => s.Position).FirstOrDefaultAsync());
    Assert.AreSame(paged, reader.LastCommands.Single(), "the page per owner was cut for all three at once");
  }

  sealed class StuckContext : SynchronizationContext {
    public int Posts;
    public override void Post(SendOrPostCallback d, object? state) => Interlocked.Increment(ref Posts);
    public override void Send(SendOrPostCallback d, object? state) => Interlocked.Increment(ref Posts);
  }

  [TestMethod]
  public async Task QueryablePropertyIncludes_RunOverEveryOwnersRows() {
    // An include on a query rooted at a queryable property runs over the batch that answers it for every owner.
    await using var writer = NewSession();
    var ids = new List<Guid>();
    foreach (var (name, phoneCount) in new[] { ("Incl-A", 2), ("Incl-B", 1), ("Incl-C", 0) }) {
      var address = writer.NewEntity<Dml.IAddress>();
      address.Id = Guid.NewGuid();
      address.City = name + " St";
      var person = writer.NewEntity<Dml.IPerson>();
      person.Id = Guid.NewGuid();
      person.LastName = name;
      person.Address = address;
      ids.Add(person.Id);
      for (var i = 0; i < phoneCount; i++) {
        var phone = writer.NewEntity<Dml.IPhone>();
        phone.Id = Guid.NewGuid();
        phone.Number = $"555-04{i}{ids.Count}";
        person.Phones.Add(phone);
      }
    }
    await writer.SaveChangesAsync();

    await using var reader = NewSession();
    var people = (await reader.ExecuteQueryAsync(
        reader.From("Person").Where(row => row.Member("Id").In(QueryMaker.Parameter("ids", typeof(Guid[]))))
          .OrderBy(row => row.Member("LastName")),
        [ids.ToArray()]))
      .Select(r => (Dml.IPerson)r.EntityInstance).ToList();
    var addresses = people.Select(p => ((EntityBase)p.Address!).Record).ToList();
    Assert.IsTrue(addresses.All(a => a.Status == EntityStatus.Stub));

    // The include reaches the phones' persons' addresses: loaded by the query, for the first owner and the second,
    // with nothing touched afterwards.
    var phones = await people[0].PhoneQuery.Include(p => p.Person.Address).OrderBy(p => p.Number)
      .ToListAsync();
    Assert.AreEqual(2, phones.Count);
    Assert.AreEqual(EntityStatus.Loaded, addresses[0].Status, "the first owner's address came with the include");
    Assert.AreEqual(EntityStatus.Loaded, addresses[1].Status, "and the second's, whose rows the batch brought too");
    var after = reader.LastCommands;
    var sibling = await people[1].PhoneQuery.Include(p => p.Person.Address).OrderBy(p => p.Number)
      .ToListAsync();
    Assert.AreEqual(1, sibling.Count);
    Assert.AreSame(after, reader.LastCommands, "the sibling was answered from what was kept");
    Assert.AreEqual("Incl-B St", people[1].Address!.City);
    Assert.AreSame(after, reader.LastCommands, "its address was there already");
  }
}
