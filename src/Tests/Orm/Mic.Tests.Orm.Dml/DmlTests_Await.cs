using Mic.Orm;
using Mic.Orm.Awaiting;
using Mic.Orm.Runtime;

namespace Mic.Tests.Orm.Db;

// AWAIT ON AN ENTITY AND ON A LIST MEMBER: the load a getter would run on the calling thread, run asynchronously.
// The namespace that makes an entity awaitable is imported by this file alone.
public abstract partial class DmlTests {
  [TestMethod]
  public async Task Await_LoadsAStubAndAListMember() {
    var tag = "await-" + Guid.NewGuid().ToString("N")[..6];
    Guid personId;
    await using (var writer = NewSession()) {
      var address = writer.NewEntity<Dml.IAddress>();
      address.Id = Guid.NewGuid();
      address.City = tag;
      var person = writer.NewEntity<Dml.IPerson>();
      person.Id = personId = Guid.NewGuid();
      person.LastName = tag;
      person.Address = address;
      foreach (var number in new[] { "555-0301", "555-0302" }) {
        var phone = writer.NewEntity<Dml.IPhone>();
        phone.Id = Guid.NewGuid();
        phone.Number = number;
        person.Phones.Add(phone);
      }
      await writer.SaveChangesAsync();
    }

    await using var session = NewSession();
    var loaded = await session.GetEntityAsync<Dml.IPerson>(personId);

    // ----- a reference is a stub; awaiting it loads it and returns it ----------------------------------------------
    var stub = loaded!.Address;
    Assert.AreEqual(EntityStatus.Stub, EntityHelper.GetRecord(stub!).Status);
    var commands = session.LastCommands;
    var home = await stub;
    Assert.AreSame(stub, home, "the entity awaited is the entity returned");
    Assert.AreEqual(EntityStatus.Loaded, EntityHelper.GetRecord(home!).Status);
    Assert.AreNotSame(commands, session.LastCommands, "the row was read");
    Assert.AreEqual(tag, home!.City);

    // ----- an entity already loaded runs nothing ----------------------------------------------------------------
    commands = session.LastCommands;
    Assert.AreSame(home, await loaded.Address);
    Assert.AreSame(commands, session.LastCommands);

    // ----- a reference that is null awaits to null ---------------------------------------------------------------
    Assert.IsNull(await loaded.Manager);

    // ----- a list member is loaded, and returned ----------------------------------------------------------------
    var phones = loaded.Phones;
    Assert.IsFalse(((IMemberEntityList)phones).IsLoaded);
    var listed = await phones;
    Assert.AreSame(phones, listed);
    Assert.IsTrue(((IMemberEntityList)phones).IsLoaded);
    Assert.AreEqual(2, listed.Count);
    commands = session.LastCommands;
    Assert.AreEqual(2, (await loaded.Phones).Count);
    Assert.AreSame(commands, session.LastCommands, "a loaded list runs nothing");

    // ----- what is no entity, and a list that is no list member, compile here and are refused -----------------------
    await Assert.ThrowsExactlyAsync<ArgumentException>(async () => await "not an entity");
    IList<Dml.IPerson> plain = new List<Dml.IPerson>();
    await Assert.ThrowsExactlyAsync<ArgumentException>(async () => await plain);
  }
}
