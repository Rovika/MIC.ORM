using Mic.Linq;
using Mic.Orm;
using Mic.Orm.Db;
using Mic.Orm.Runtime;

namespace Mic.Tests.Orm.Db;

// A SESSION OPENED WITHOUT AddThreadSafety makes nobody wait: an operation started while another is on the connection
// is refused, and so is a change to the session's entities while a save writes them. Each is provoked from a handler
// that runs inside the operation, `Loaded` while a query reads its rows and `CommandExecuted` before a save accepts
// what it wrote, so that the refusals do not depend on how soon a driver's call returns.
public abstract partial class DmlTests {
  const string RunningMessage = "The session is already running an operation";
  const string SavingMessage = "The session is saving its changes";

  [TestMethod]
  public async Task Session_RefusesAnOperationStartedWhileAnotherRuns() {
    var tag = "one-" + Guid.NewGuid().ToString("N")[..6];
    var app = NewApp();
    await using (var writer = new EntitySession(NewContext(app))) {
      for (var i = 0; i < 2; i++) {
        var person = writer.NewEntity<Dml.IPerson>();
        person.Id = Guid.NewGuid();
        person.LastName = tag + i;
      }
      await writer.SaveChangesAsync();
    }

    await using var session = new EntitySession(NewContext(app));

    // ----- started while the first is still on the connection: refused, and the first is not disturbed --------------
    Task<IReadOnlyList<Dml.IPerson>>? overlapping = null;
    app.Model!.FindEntity("Person")!.Events.Loaded += loaded => {
      if (loaded.Session == session)
        overlapping ??= session.EntitySet<Dml.IPerson>().Where(p => p.LastName == tag + "1").ToListAsync();
    };
    var first = await session.EntitySet<Dml.IPerson>().Where(p => p.LastName == tag + "0").ToListAsync();
    Assert.AreEqual(1, first.Count);
    Assert.IsNotNull(overlapping, "the handler did not run");
    var refused = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => overlapping);
    StringAssert.Contains(refused.Message, RunningMessage);
    StringAssert.Contains(refused.Message, nameof(EntitySessionOptions.AddThreadSafety));

    // ----- the session is as it was: the next operation, awaited, runs ----------------------------------------------
    Assert.AreEqual(2, await session.EntitySet<Dml.IPerson>().CountAsync(p => p.LastName.StartsWith(tag)));

    // ----- two started together, as an application writes it: the first always answers; the second answers where the
    // driver had finished the first before returning, and is refused in the ORM's words otherwise ---------------------
    var one = session.EntitySet<Dml.IPerson>().Where(p => p.LastName == tag + "0").ToListAsync();
    var two = session.EntitySet<Dml.IPerson>().Where(p => p.LastName == tag + "1").ToListAsync();
    Assert.AreEqual(tag + "0", (await one).Single().LastName);
    try {
      Assert.AreEqual(tag + "1", (await two).Single().LastName);
    } catch (InvalidOperationException overlap) {
      StringAssert.Contains(overlap.Message, RunningMessage);
    }
    Assert.AreEqual(2, await session.EntitySet<Dml.IPerson>().CountAsync(p => p.LastName.StartsWith(tag)));
  }

  [TestMethod]
  public async Task Session_RefusesAChangeMadeWhileItSaves() {
    var tag = "busy-" + Guid.NewGuid().ToString("N")[..6];
    var app = NewApp();
    var otherId = Guid.NewGuid();
    await using (var writer = new EntitySession(NewContext(app))) {
      var other = writer.NewEntity<Dml.IPerson>();
      other.Id = otherId;
      other.LastName = tag + "-other";
      await writer.SaveChangesAsync();
    }

    await using var session = new EntitySession(NewContext(app));
    var address = session.NewEntity<Dml.IAddress>();
    address.Id = Guid.NewGuid();
    address.City = tag;
    var person = session.NewEntity<Dml.IPerson>();
    person.Id = Guid.NewGuid();
    person.LastName = tag;
    await session.SaveChangesAsync();

    // ----- every way to change the session, tried while the save still holds the pending set ------------------------
    var refusals = new List<Exception?>();
    Task? secondSave = null;
    var saving = false;
    app.Events.CommandExecuted += (ran, _) => {
      if (ran != session || !saving || refusals.Count > 0)
        return;
      refusals.Add(Catch(() => person.LastName = tag + "-late"));
      refusals.Add(Catch(() => session.NewEntity<Dml.IPerson>()));
      refusals.Add(Catch(() => session.DeleteEntity(person)));
      refusals.Add(Catch(() => EntityHelper.MarkModified(person, nameof(Dml.IPerson.Rank))));
      refusals.Add(Catch(() => address.Residents.Add(person)));
      refusals.Add(Catch(() => session.EntitySet<Dml.IPerson>().Where(p => p.Id == otherId).ScheduleDelete()));
      secondSave = session.SaveChangesAsync();
      // The commands have run: a read is no overlap, and the handler may make one.
      Assert.AreEqual(tag + "-other", session.GetEntity<Dml.IPerson>(otherId)!.LastName);
    };
    person.Rank = 3;
    saving = true;
    await session.SaveChangesAsync();
    saving = false;

    Assert.AreEqual(6, refusals.Count, "the handler did not run");
    foreach (var refusal in refusals) {
      Assert.IsInstanceOfType<InvalidOperationException>(refusal);
      StringAssert.Contains(refusal!.Message, SavingMessage);
    }
    var again = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => secondSave!);
    StringAssert.Contains(again.Message, SavingMessage);

    // ----- nothing of it reached the session or the database; the same change after the save is saved ---------------
    Assert.AreEqual(tag, person.LastName);
    Assert.AreEqual(EntityStatus.Loaded, EntityHelper.GetRecord(person).Status);
    Assert.AreEqual(0, session.ScheduledMutations.Count);
    person.LastName = tag + "-after";
    await session.SaveChangesAsync();
    await using (var reader = NewSession()) {
      var stored = await reader.GetEntityAsync<Dml.IPerson>(person.Id);
      Assert.AreEqual(tag + "-after", stored!.LastName);
      Assert.AreEqual(3, stored.Rank);
    }

    // ----- a save that failed lets go as well ----------------------------------------------------------------------
    var clash = session.NewEntity<Dml.IPerson>();
    clash.Id = otherId;
    clash.LastName = tag + "-clash";
    await Assert.ThrowsExactlyAsync<DataAccessException>(() => session.SaveChangesAsync());
    session.DeleteEntity(clash);
    person.Rank = 4;
    await session.SaveChangesAsync();

    // ----- local functions
    static Exception? Catch(Action change) {
      try {
        change();
        return null;
      } catch (Exception refused) {
        return refused;
      }
    }
  }
}
