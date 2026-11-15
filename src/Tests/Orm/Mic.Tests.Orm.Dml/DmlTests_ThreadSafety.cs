using Mic.Linq;
using Mic.Orm;
using Mic.Orm.Runtime;

namespace Mic.Tests.Orm.Db;

// A SESSION OPENED WITH AddThreadSafety against a real server: operations started together run in turn on the
// session's one connection, and a handler that reads the session inside a save is let through.
public abstract partial class DmlTests {
  [TestMethod]
  public async Task ThreadSafeSession_RunsOverlappingOperationsInTurn() {
    var tag = "turn-" + Guid.NewGuid().ToString("N")[..6];
    var app = NewApp();
    var personId = Guid.NewGuid();
    await using (var writer = new EntitySession(NewContext(app))) {
      for (var i = 0; i < 2; i++) {
        var person = writer.NewEntity<Dml.IPerson>();
        person.Id = i == 0 ? personId : Guid.NewGuid();
        person.LastName = tag + i;
      }
      await writer.SaveChangesAsync();
    }

    await using var session = new EntitySession(NewContext(app), EntitySessionOptions.AddThreadSafety);

    // ----- two queries and a save started together, none awaited before the next starts -----------------------------
    var first = session.EntitySet<Dml.IPerson>().Where(p => p.LastName == tag + "0").ToListAsync();
    var second = session.EntitySet<Dml.IPerson>().Where(p => p.LastName == tag + "1").ToListAsync();
    var third = session.NewEntity<Dml.IPerson>();
    third.Id = Guid.NewGuid();
    third.LastName = tag + "2";
    var save = session.SaveChangesAsync();
    await Task.WhenAll(first, second, save);
    Assert.AreEqual(1, first.Result.Count);
    Assert.AreEqual(tag + "1", second.Result.Single().LastName);
    Assert.AreEqual(3, await session.EntitySet<Dml.IPerson>().CountAsync(p => p.LastName.StartsWith(tag)));

    // ----- a handler reads the session after a command of a save: a call from inside the operation ------------------
    string? seenName = null;
    var reading = false;
    app.Events.CommandExecuted += (ran, _) => {
      if (ran != session || reading)
        return;
      reading = true;
      seenName = session.GetEntity<Dml.IPerson>(personId)!.LastName;
      reading = false;
    };
    var ticket = session.NewEntity<Dml.ITicket>();
    ticket.Title = tag;
    await session.SaveChangesAsync();
    Assert.AreEqual(tag + "0", seenName, "the handler's read did not run");
  }
}
