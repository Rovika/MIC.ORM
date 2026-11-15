using Mic.Linq;
using Mic.Orm;

namespace Mic.Tests.Orm.Db;

// A MEMBER A METHOD COMPUTES on the client: read off a loaded entity, following its sources, nowhere in the table.
public abstract partial class DmlTests {
  [TestMethod]
  public async Task ComputedMember_IsComputedOverTheLoadedEntity() {
    await using var writer = NewSession();
    var person = writer.NewEntity<Dml.IPerson>();
    person.Id = Guid.NewGuid();
    person.LastName = "Lee";
    person.Rank = 3;
    Assert.AreEqual("Lee (#3)", person.DisplayName);
    await writer.SaveChangesAsync();

    await using var reader = NewSession();
    var loaded = await reader.GetEntityAsync<Dml.IPerson>(person.Id);
    Assert.AreEqual("Lee (#3)", loaded.DisplayName);
    loaded.Rank = 4;
    Assert.AreEqual("Lee (#4)", loaded.DisplayName, "computed again on the next read");
  }

  [TestMethod]
  public async Task ComputedMember_PersistedIsWrittenAtEverySave() {
    await using var writer = NewSession();
    var person = writer.NewEntity<Dml.IPerson>();
    person.Id = Guid.NewGuid();
    person.LastName = "Lee";
    Assert.AreEqual(3, person.NameLength, "computed on a read before any save");
    await writer.SaveChangesAsync();
    Assert.AreEqual(3, await ProbeScalarAsync<int>(
      $"SELECT {Db.Quote("NameLength")} FROM {Db.GetTableRef("Person")} WHERE {Db.Quote("Id")} = @id", ("id", person.Id)));

    // ----- a query filters on the column ------------------------------------------------------------------------
    await using var reader = NewSession();
    Assert.AreEqual(1, await reader.EntitySet<Dml.IPerson>().CountAsync(p => p.NameLength == 3 && p.Id == person.Id));
    Assert.AreEqual(0, await reader.EntitySet<Dml.IPerson>().CountAsync(p => p.NameLength == 6 && p.Id == person.Id));

    // ----- the column follows its sources through an update -------------------------------------------------------
    var loaded = await reader.GetEntityAsync<Dml.IPerson>(person.Id);
    loaded.LastName = "Parker";
    Assert.AreEqual(6, loaded.NameLength);
    await reader.SaveChangesAsync();
    Assert.AreEqual(6, await ProbeScalarAsync<int>(
      $"SELECT {Db.Quote("NameLength")} FROM {Db.GetTableRef("Person")} WHERE {Db.Quote("Id")} = @id", ("id", person.Id)));
  }
}
