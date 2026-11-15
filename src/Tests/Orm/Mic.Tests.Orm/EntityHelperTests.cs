using Mic.Orm;
using Mic.Orm.Model;
using Mic.Orm.Runtime;

namespace Mic.Tests.Orm;

// WHAT AN ENTITY CANNOT ANSWER ABOUT ITSELF. An entity arrives as its interface, and everything the ORM knows about it
// lives on the record behind that. These are the questions application code used to answer with
// `((EntityBase)(object)person).Record` — a cast that compiles for any type at all.
//
// Nothing here touches a database.
[TestClass]
public class EntityHelperTests {
  public interface IPerson {
    [PrimaryKey] Guid Id { get; set; }
    [Size(50)] string LastName { get; set; }
    int Rank { get; set; }
  }

  static EntitySession NewSession() {
    var app = new EntityApp("App", new Version(1, 0), new EntityAppSettings { DefaultSchema = "app" });
    var module = new EntityModule("M", new Version(1, 0));
    module.RegisterEntities(typeof(IPerson));
    app.AddModule(module);
    app.TryInit();
    app.WithSqlite();
    Assert.IsTrue(app.Initialized, app.Report());
    return new EntitySession(new OperationContext(app));
  }

  static IPerson NewPerson(EntitySession session) {
    var person = session.NewEntity<IPerson>();
    person.LastName = "Larsen";
    person.Rank = 3;
    return person;
  }

  // ----- ONE entity, every question the helper answers about it ----------------------------------------------------------
  //
  // Eight methods over one session. All of it is the same argument: an entity arrives as its interface, and the helper
  // is what asks the record behind it — by NAME, and refusing by name too.
  [TestMethod]
  public void Helper_ReachesRecordTypeAndValues() {
    var session = NewSession();
    var person = NewPerson(session);

    var record = EntityHelper.GetRecord(person);
    Assert.AreEqual("Person", record.Entity.Name);
    Assert.AreEqual(EntityStatus.New, record.Status);
    Assert.AreSame(session, EntityHelper.GetSession(person));

    // ----- and the TYPE it answers with is the INTERFACE ---------------------------------------------------------------
    //
    // `GetType()` answers with a runtime-generated class whose name no developer wrote and which nothing in the model
    // knows — which is exactly why this exists.
    Assert.AreEqual(typeof(IPerson), EntityHelper.GetEntityType(person));
    Assert.AreNotEqual(typeof(IPerson), person.GetType());
    Assert.AreEqual("Person", EntityHelper.GetEntityInfo(person).Name);

    // ----- properties are read and written BY NAME ----------------------------------------------------------------------
    Assert.AreEqual("Larsen", EntityHelper.GetProperty(person, "LastName"));
    Assert.AreEqual(3, EntityHelper.GetProperty<int>(person, "Rank"));

    EntityHelper.SetProperty(person, "LastName", "Larsson");
    Assert.AreEqual("Larsson", person.LastName, "the write has to land on the same slot the property reads");

    // …case-insensitively, as the model matches names everywhere else. A name reaching here has usually come from a
    // document, a database or a designer, and none of them is reliably cased the way the interface was declared.
    Assert.AreEqual("Larsson", EntityHelper.GetProperty(person, "lastname"));
    EntityHelper.SetProperty(person, "RANK", 7);
    Assert.AreEqual(7, person.Rank);

    // ----- a typed read of the WRONG type is `default`, not a throw ------------------------------------------------------
    //
    // The same shape `EntityRecord.GetValue<T>` has. An emitted property is compiled against a member whose type is
    // known; a by-name read is not, and a caller asking for the wrong one is guessing either way.
    Assert.AreEqual(0, EntityHelper.GetProperty<int>(person, "LastName"));
    Assert.IsNull(EntityHelper.GetProperty<string>(person, "Rank"));

    // ----- while an UNKNOWN name is refused, naming the entity and the member ----------------------------------------------
    var unknown = Assert.ThrowsExactly<KeyNotFoundException>(() => EntityHelper.GetProperty(person, "NoSuchThing"));
    StringAssert.Contains(unknown.Message, "Person");
    StringAssert.Contains(unknown.Message, "NoSuchThing");

    // ----- and so is a NON-ENTITY, by name -----------------------------------------------------------------------------------
    //
    // The point of the helper over the cast it replaces: `((EntityBase)(object)"x").Record` throws
    // `InvalidCastException: Unable to cast object of type 'String'`, leaving the reader to work out what it should
    // have been.
    var notAnEntity = Assert.ThrowsExactly<ArgumentException>(() => EntityHelper.GetRecord("not an entity"));
    StringAssert.Contains(notAnEntity.Message, "is not an entity");
    StringAssert.Contains(notAnEntity.Message, "String");

    // ----- with no database there is nothing to have RUN, and that is an empty list rather than a null -------------------------
    Assert.AreEqual(0, EntityHelper.GetLastCommands(person).Count);
  }

  // The time a generated Guid carries, read back out of either layout; a Guid that carries none says so.
  [TestMethod]
  public void GetTimestamp_ReadsTheTimeAGeneratedGuidCarries() {
    var app = NewSession().Context.App;
    var guids = new Mic.Orm.Services.GuidFactory(app);
    var instant = new DateTime(2031, 4, 1, 13, 45, 12, 345, DateTimeKind.Utc);

    Assert.AreEqual(instant, EntityHelper.GetTimestamp(guids.NewGuid(GuidScheme.V7, instant)));
    Assert.AreEqual(instant, EntityHelper.GetTimestamp(guids.NewGuid(GuidScheme.V8SqlServer, instant)));
    Assert.AreEqual(DateTimeKind.Utc, EntityHelper.GetTimestamp(guids.NewGuid(GuidScheme.V7, instant))!.Value.Kind);
    Assert.AreEqual(instant, EntityHelper.GetTimestamp(Guid.CreateVersion7(instant)), "the framework's own version 7");

    Assert.IsNull(EntityHelper.GetTimestamp(guids.NewGuid(GuidScheme.V4)));
    Assert.IsNull(EntityHelper.GetTimestamp(Guid.Empty));
  }
}
