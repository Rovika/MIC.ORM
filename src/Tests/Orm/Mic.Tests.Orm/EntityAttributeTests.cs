using System.Reflection;
using Mic.Orm;

namespace Mic.Tests.Orm;

// The attribute vocabulary, exercised the way a developer writes it: a decorated interface, read back. Nothing
// interprets these yet — the builder is a later step — so what is pinned down here is that the vocabulary EXPRESSES
// the model, including the awkward cases the test model was designed around.
[TestClass]
public class EntityAttributeTests {
  [Entity(TableName = "people")]
  [PrimaryKey("Id")]
  [Index("LastName -DateCreated", IncludeMembers = "FirstName", Filter = "[LastName] IS NOT NULL")]
  [OrderBy("LastName FirstName")]
  [OldNames("IPersonOld")]
  [Validator(typeof(EntityAttributeTests), nameof(ValidatePerson))]
  private interface IPerson {
    [Auto(AutoType.NewGuid)] Guid Id { get; }
    [Size(80)] string LastName { get; set; }
    string? MiddleName { get; set; }              // nullability from the CLR type, no attribute needed
    [Nullable] string FirstName { get; set; }     // and the explicit form, still supported
    [Unique] string Email { get; set; }
    [NoUpdate] DateTime DateCreated { get; set; }
    [NoColumn] string FullName { get; }
    [Secret] string PasswordHash { get; set; }
    [HashFor(nameof(Email))] int EmailHash { get; set; }
    IAddress Address { get; set; }
    [OneToMany(nameof(ITripLeg.Trip))] IList<ITripLeg> Legs { get; }
  }

  private interface IAddress {
    [PrimaryKey] Guid Id { get; }
    [Size("ShortText", 40)] string City { get; set; }
    [Unlimited] string Notes { get; set; }
  }

  // The composite-key case the test model exists to exercise.
  [PrimaryKey("TripId,LegNo")]
  private interface ITripLeg {
    IPerson Trip { get; set; }
    int LegNo { get; set; }
  }

  private static void ValidatePerson(IPerson person) { }

  private static T? AttrOn<T>(Type type) where T : Attribute => type.GetCustomAttribute<T>();

  private static T? AttrOn<T>(Type type, string member) where T : Attribute =>
    type.GetProperty(member)!.GetCustomAttribute<T>();

  // ----- the vocabulary, read back off one decorated model ---------------------------------------------------------------
  //
  // Eleven methods of pure reflection over the same three interfaces. Nothing interprets any of it yet — what is pinned
  // down is that the vocabulary EXPRESSES the model, including the awkward cases the test model was designed around.
  [TestMethod]
  public void AttributeVocabulary_ExpressesTheModelAtBothLevels() {
    // ----- entity level ---------------------------------------------------------------------------------------------
    Assert.AreEqual("people", AttrOn<EntityAttribute>(typeof(IPerson))!.TableName);
    Assert.AreEqual("Id", AttrOn<PrimaryKeyAttribute>(typeof(IPerson))!.MembersSpec);
    Assert.AreEqual("IPersonOld", AttrOn<OldNamesAttribute>(typeof(IPerson))!.OldNames);
    Assert.AreEqual("LastName FirstName", AttrOn<OrderByAttribute>(typeof(IPerson))!.OrderByList);

    var index = AttrOn<IndexAttribute>(typeof(IPerson))!;
    Assert.AreEqual("LastName -DateCreated", index.MembersSpec);
    Assert.AreEqual("FirstName", index.IncludeMembers);
    Assert.AreEqual("[LastName] IS NOT NULL", index.Filter);
    Assert.IsFalse(index.Unique);

    // A COMPOSITE key can only be said at entity level — which is the argument against inferring a key from a member
    // named Id: the convention would not remove the attribute, only make it sometimes-optional.
    Assert.AreEqual("TripId,LegNo", AttrOn<PrimaryKeyAttribute>(typeof(ITripLeg))!.MembersSpec);

    // ----- member level ---------------------------------------------------------------------------------------------
    var pk = AttrOn<PrimaryKeyAttribute>(typeof(IAddress), nameof(IAddress.Id))!;
    Assert.IsNotNull(pk);
    Assert.IsNull(pk.MembersSpec, "on a property it needs no member list");

    // [Unique] IS an Index with Unique set — kept as the friendlier spelling, not as a separate concept.
    var unique = AttrOn<UniqueAttribute>(typeof(IPerson), nameof(IPerson.Email))!;
    Assert.IsTrue(unique.Unique);
    Assert.IsInstanceOfType<IndexAttribute>(unique);

    // …as [Identity], [RowVersion] and [Sequence] are all [Auto] underneath.
    Assert.AreEqual(AutoType.Identity, new IdentityAttribute().Type);
    Assert.AreEqual(AutoType.RowVersion, new RowVersionAttribute().Type);
    var sequence = new SequenceAttribute("dbo.OrderNumbers");
    Assert.AreEqual(AutoType.Sequence, sequence.Type);
    Assert.AreEqual("dbo.OrderNumbers", sequence.SequenceName);

    // ----- size has both a direct value and a NAMED CODE ---------------------------------------------------------------
    //
    // The named form is how an application adjusts the column sizes of a module it imported but did not write.
    Assert.AreEqual(80, AttrOn<SizeAttribute>(typeof(IPerson), nameof(IPerson.LastName))!.Size);
    var coded = AttrOn<SizeAttribute>(typeof(IAddress), nameof(IAddress.City))!;
    Assert.AreEqual("ShortText", coded.SizeCode);
    Assert.AreEqual(40, coded.Size);                 // the fallback when the app names no size for that code

    // ----- while NULLABILITY needs no attribute at all -------------------------------------------------------------------
    //
    // The language says it and the compiler checks it. The attribute is for what the language cannot express, and for
    // assemblies compiled without nullable reference types.
    var context = new NullabilityInfoContext();
    Assert.AreEqual(NullabilityState.Nullable,
      context.Create(typeof(IPerson).GetProperty(nameof(IPerson.MiddleName))!).ReadState);
    Assert.AreEqual(NullabilityState.NotNull,
      context.Create(typeof(IPerson).GetProperty(nameof(IPerson.LastName))!).ReadState);
    Assert.IsNotNull(AttrOn<NullableAttribute>(typeof(IPerson), nameof(IPerson.FirstName)));

    // ----- generated values, policy markers, and the one that names another member ------------------------------------------
    Assert.AreEqual(AutoType.NewGuid, AttrOn<AutoAttribute>(typeof(IPerson), nameof(IPerson.Id))!.Type);
    Assert.IsNotNull(AttrOn<NoUpdateAttribute>(typeof(IPerson), nameof(IPerson.DateCreated)));
    Assert.IsNotNull(AttrOn<NoColumnAttribute>(typeof(IPerson), nameof(IPerson.FullName)));
    Assert.IsNotNull(AttrOn<SecretAttribute>(typeof(IPerson), nameof(IPerson.PasswordHash)));
    Assert.IsNotNull(AttrOn<UnlimitedAttribute>(typeof(IAddress), nameof(IAddress.Notes)));
    Assert.AreEqual(nameof(IPerson.Email),
      AttrOn<HashForAttribute>(typeof(IPerson), nameof(IPerson.EmailHash))!.PropertyName);

    // ----- and every one of them shares ONE base ----------------------------------------------------------------------------
    //
    // What lets a builder collect them in a single pass rather than asking for each type by name.
    var attributes = typeof(IPerson).GetCustomAttributes<EntityModelAttributeBase>().ToList();
    Assert.IsTrue(attributes.Count >= 5);
    Assert.IsTrue(attributes.Any(a => a is EntityAttribute));
    Assert.IsTrue(attributes.Any(a => a is PrimaryKeyAttribute));
  }

  [TestMethod]
  public void DroppedVitaAttributes_AreAbsent() {
    // A ported model naming one of these should fail to COMPILE, loudly, rather than bind to something that no longer
    // does what it did. Guards against one being re-added by reflex.
    foreach (var name in new[] {
        "TransactionIdAttribute", "PropagateUpdatedOnAttribute", "DoNotTrack", "DiscardOnAbortAttribute",
        "DisplayAttribute", "GrantAccessAttribute", "PropertyGroupAttribute", "GroupsAttribute" }) {
      Assert.IsNull(typeof(EntityAttribute).Assembly.GetType("Mic.Orm." + name), $"{name} should not exist");
    }
  }
}
