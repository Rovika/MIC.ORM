using Mic.Orm;
using Mic.Orm.Model;
using Mic.Orm.Runtime;

namespace Mic.Tests.Orm;

// WHAT THE ORM ITSELF CHECKS, which is two things: a value MISSING where the model says it cannot be, and a string
// LONGER than the size the model gives it. Everything else is an application's rule and goes in a `ValidatingChanges`
// handler. Nothing here touches a database — validation happens before a save is planned, over records in hand.
[TestClass]
public class ValidationTests {
  public interface ICustomer {
    [PrimaryKey, Auto(AutoType.NewGuid)] Guid Id { get; set; }
    [Size(50)] string LastName { get; set; }                       // required: not nullable
    [Size(20)] string? Nickname { get; set; }                      // optional
    [Size(10, SizeOptions.AutoTrim)] string? Tag { get; set; }     // cut rather than refused
    [Unlimited] string? Notes { get; set; }                        // no limit to exceed
    int Rank { get; set; }
    IRegion Region { get; set; }                                   // required reference
    IRegion? Fallback { get; set; }                                // optional reference
  }

  public interface IRegion {
    [PrimaryKey, Auto(AutoType.NewGuid)] Guid Id { get; set; }
    [Size(30)] string Name { get; set; }
  }

  /// <summary>A key the APPLICATION is expected to supply — no `[Auto]`, and no server to make one either.</summary>
  public interface IManualKey {
    [PrimaryKey] Guid Id { get; set; }
    [Size(20)] string Name { get; set; }
  }

  static EntityApp NewApp() {
    var app = new EntityApp("Sales", new Version(1, 0), new EntityAppSettings { DefaultSchema = "app" });
    var module = new EntityModule("M", new Version(1, 0));
    module.RegisterEntities(typeof(ICustomer), typeof(IRegion), typeof(IManualKey));
    app.AddModule(module);
    app.TryInit();
    app.WithSqlite();
    Assert.IsTrue(app.Initialized, app.Report());
    return app;
  }

  /// <summary>A customer with everything the model demands — the baseline each test then breaks one way.</summary>
  static (EntitySession Session, ICustomer Customer) Valid(EntityApp app) {
    var session = new EntitySession(new OperationContext(app));
    var region = session.NewEntity<IRegion>();
    region.Name = "North";
    var customer = session.NewEntity<ICustomer>();
    customer.LastName = "Larsen";
    customer.Region = region;
    return (session, customer);
  }

  // ----- the baseline ---------------------------------------------------------------------------------------------

  // ----- ONE model, every way a value can be MISSING or not ----------------------------------------------------------
  //
  // Eight methods, each building the same application to break the same baseline one way. The application is built
  // once here and each case gets its own session, which is the part that has to be independent.
  [TestMethod]
  public void MissingValue_IsAFaultAnEmptySlotIsNot() {
    var app = NewApp();

    // ----- the baseline passes QUIETLY, optional members and all ------------------------------------------------------
    var (baseline, spare) = Valid(app);
    spare.Nickname = null;                                          // optional value, and no Fallback either
    baseline.ValidateChanges();

    // ----- a required value left null is a fault ----------------------------------------------------------------------
    var (nulled, nullCustomer) = Valid(app);
    nullCustomer.LastName = null!;

    var fault = Assert.ThrowsExactly<ClientFaultException>(nulled.ValidateChanges).Faults.Single();
    Assert.AreEqual(ClientFaultCodes.ValueMissing, fault.Code);
    Assert.AreEqual("LastName", fault.MemberName);

    // ----- and so is an EMPTY STRING ----------------------------------------------------------------------------------
    //
    // Nothing in a form tells "left blank" from "cleared", the database would take either, and an application asking
    // for a required name does not mean "any characters at all".
    var (blanked, blankCustomer) = Valid(app);
    blankCustomer.LastName = "";

    Assert.AreEqual(ClientFaultCodes.ValueMissing,
      Assert.ThrowsExactly<ClientFaultException>(blanked.ValidateChanges).Faults.Single().Code);

    // ----- a required REFERENCE left empty names the REFERENCE ---------------------------------------------------------
    var unregioned = new EntitySession(new OperationContext(app));
    var customer = unregioned.NewEntity<ICustomer>();
    customer.LastName = "Larsen";                                 // but no Region

    var missing = Assert.ThrowsExactly<ClientFaultException>(unregioned.ValidateChanges).Faults.Single();
    Assert.AreEqual("Region", missing.MemberName, "the REFERENCE is named, not the foreign key member behind it");
    Assert.AreEqual(ClientFaultCodes.ValueMissing, missing.Code);

    // ----- while a VALUE TYPE nobody assigned is not missing at all -----------------------------------------------------
    //
    // A record starts at its defaults, so an int nobody touched holds a perfectly good zero — in the SLOT, not merely
    // as what a reader coalesces to. Validation reads the slot and finds a value, which is what it is.
    Assert.AreEqual(0, spare.Rank);
    Assert.AreEqual(0, EntityHelper.GetRecord(spare).GetRawValue(
      app.Model!.FindEntity("Customer")!.GetMember("Rank")), "the default is IN the slot");

    // ----- while a required KEY nobody assigned IS missing -----------------------------------------------------------------
    //
    // The one member kind whose slot is left empty, and the reason it is: a key has no default that means anything.
    // Reported HERE rather than written as `Guid.Empty` — which the server would accept once and refuse the second
    // time, so a row would succeed or fail depending on whether it happened to be first.
    var manual = new EntitySession(new OperationContext(app));
    manual.NewEntity<IManualKey>().Name = "no key";

    var noKey = Assert.ThrowsExactly<ClientFaultException>(manual.ValidateChanges).Faults.Single();
    Assert.AreEqual(ClientFaultCodes.ValueMissing, noKey.Code);
    Assert.AreEqual("Id", noKey.MemberName);

    // …and supplying one settles it. A key the ORM or the SERVER makes never reaches this check at all — every other
    // entity in this model carries [Auto(NewGuid)] and validates clean above.
    var keyed = new EntitySession(new OperationContext(app));
    var supplied = keyed.NewEntity<IManualKey>();
    supplied.Id = Guid.NewGuid();
    supplied.Name = "keyed";
    keyed.ValidateChanges();

    // ----- and a DELETED record is not checked at all --------------------------------------------------------------------
    //
    // Nothing to check on the way out: the values it still carries are the ones it had, not ones anybody is storing.
    var (deleting, doomed) = Valid(app);
    var record = EntityHelper.GetRecord(doomed);
    record.Status = EntityStatus.Loaded;
    record.AcceptChanges();
    record.SetValue(record.Entity.GetMember("LastName"), null);
    deleting.DeleteEntity(doomed);

    deleting.ValidateChanges();
  }

  [TestMethod]
  public void AReferenceToARowWhoseKeyTheSERVERWillMakeIsNotMissing() {
    // The child's foreign key members are EMPTY at this moment: the parent has not been inserted, so there is no key
    // to copy, and it is filled in mid-save. The reference member is holding the parent all along.
    var app = new EntityApp("Support", new Version(1, 0), new EntityAppSettings { DefaultSchema = "app" });
    var module = new EntityModule("M", new Version(1, 0));
    module.RegisterEntities(typeof(ITicket), typeof(IComment));
    app.AddModule(module);
    app.TryInit();
    app.WithSqlite();
    Assert.IsTrue(app.Initialized, app.Report());

    var session = new EntitySession(new OperationContext(app));
    var ticket = session.NewEntity<ITicket>();
    ticket.Title = "Broken";
    var comment = session.NewEntity<IComment>();
    comment.Ticket = ticket;
    comment.Text = "Looking at it";

    session.ValidateChanges();
  }

  public interface ITicket {
    [PrimaryKey, Auto(AutoType.Identity)] int Id { get; set; }
    [Size(40)] string Title { get; set; }
  }

  public interface IComment {
    [PrimaryKey, Auto(AutoType.NewGuid)] Guid Id { get; set; }
    ITicket Ticket { get; set; }
    [Size(100)] string Text { get; set; }
  }

  // ----- value too long -------------------------------------------------------------------------------------------

  [TestMethod]
  public void StringPastItsSize_IsAFaultUnlessUnsizedOrTrimmed() {
    var app = NewApp();

    // ----- past its size ---------------------------------------------------------------------------------------------
    var (over, tooLong) = Valid(app);
    tooLong.Nickname = new string('x', 21);

    var fault = Assert.ThrowsExactly<ClientFaultException>(over.ValidateChanges).Faults.Single();
    Assert.AreEqual(ClientFaultCodes.ValueTooLong, fault.Code);
    StringAssert.Contains(fault.Message, "21");
    StringAssert.Contains(fault.Message, "20");

    // ----- EXACTLY its size is fine, and an [Unlimited] one has no limit to exceed --------------------------------------
    var (exact, fits) = Valid(app);
    fits.Nickname = new string('x', 20);
    fits.Notes = new string('x', 10_000);
    exact.ValidateChanges();

    // ----- while AUTOTRIM cuts instead of refusing -----------------------------------------------------------------------
    var (trimming, tagged) = Valid(app);
    tagged.Tag = "abcdefghijKLMNOP";

    trimming.ValidateChanges();

    Assert.AreEqual("abcdefghij", tagged.Tag, "the FRONT is kept — trimming loses the end, not the beginning");
  }

  // ----- how faults are reported ----------------------------------------------------------------------------------

  [TestMethod]
  public void EveryFault_IsReportedNotTheFirst() {
    // A person filling in a form wants telling what to fix once, rather than one field at a time.
    var app = NewApp();
    var session = new EntitySession(new OperationContext(app));
    var first = session.NewEntity<ICustomer>();                     // no LastName, no Region
    var second = session.NewEntity<ICustomer>();
    second.LastName = new string('x', 51);                          // too long, and no Region either

    var ex = Assert.ThrowsExactly<ClientFaultException>(session.ValidateChanges);

    Assert.AreEqual(4, ex.Faults.Count, ex.Message);
    Assert.AreEqual(2, ex.Faults.Count(f => f.MemberName == "Region"));
    CollectionAssert.AreEquivalent(new[] { first, second }.Select(EntityHelper.GetRecord).ToArray(),
      ex.Faults.Select(f => f.Record).Distinct().ToArray());
  }

  [TestMethod]
  public void Faults_AreOnTheRecordAndAPassingRunClearsThem() {
    var app = NewApp();
    var (session, customer) = Valid(app);
    customer.LastName = null!;
    var record = EntityHelper.GetRecord(customer);

    Assert.ThrowsExactly<ClientFaultException>(session.ValidateChanges);

    Assert.IsFalse(record.IsValid);
    Assert.AreEqual("LastName", record.ValidationFaults!.Single().MemberName);

    // ----- and the next run starts from nothing ------------------------------------------------------------------------
    customer.LastName = "Larsen";
    session.ValidateChanges();

    Assert.IsTrue(record.IsValid);
  }

  // ----- the hooks ------------------------------------------------------------------------------------------------

  [TestMethod]
  public void ApplicationRule_GoesInValidatingChanges() {
    var app = NewApp();
    var entity = app.Model!.FindEntity("Customer")!;
    var faulty = new List<EntityRecord>();
    entity.Events.ValidatingChanges += record => {
      // A PLAIN CAST, and it is the point of the whole arrangement: this handler runs over records nobody has written
      // to, and `Rank` reads as 0 rather than as null. Written `is int` while the slots started empty, which hid the
      // defect rather than stating it.
      if ((int)record.GetValue(record.Entity.GetMember("Rank"))! > 10)
        record.AddValidationFault(new ClientFault(record, "RankTooHigh", "Rank", "Rank may not exceed 10."));
    };
    entity.Events.ValidationFailed += record => faulty.Add(record);

    // ----- a good record announces nothing ---------------------------------------------------------------------------
    var (clean, _) = Valid(app);
    clean.ValidateChanges();
    Assert.AreEqual(0, faulty.Count);

    // ----- and the application's own rule reports beside the ORM's own -------------------------------------------------
    var (session, customer) = Valid(app);
    customer.Rank = 11;

    var ex = Assert.ThrowsExactly<ClientFaultException>(session.ValidateChanges);
    Assert.AreEqual("RankTooHigh", ex.Faults.Single().Code);
    Assert.AreSame(EntityHelper.GetRecord(customer), faulty.Single(), "ValidationFailed names the faulty record");
  }

  [TestMethod]
  public void AHandlerCanFIXAValueInsteadOfComplaining() {
    // It runs BEFORE the model's own checks, so a value written there is a correction the rules then see.
    var app = NewApp();
    var entity = app.Model!.FindEntity("Customer")!;
    var lastName = entity.GetMember("LastName");
    entity.Events.ValidatingChanges += record => {
      if (string.IsNullOrEmpty(record.GetValue(lastName) as string))
        record.SetValue(lastName, "(unknown)");
    };

    var session = new EntitySession(new OperationContext(app));
    var region = session.NewEntity<IRegion>();
    region.Name = "North";
    var customer = session.NewEntity<ICustomer>();
    customer.Region = region;

    session.ValidateChanges();

    Assert.AreEqual("(unknown)", customer.LastName);
  }

  // ----- and it runs as part of a save ----------------------------------------------------------------------------

  [TestMethod]
  public void ASaveVALIDATESFirstAndSendsNothing() {
    var app = NewApp();
    var (session, customer) = Valid(app);
    customer.LastName = null!;

    Assert.ThrowsExactly<ClientFaultException>(() => session.SaveChanges());

    Assert.AreEqual(0, session.LastCommands.Count, "the database should never have been reached");
    Assert.AreEqual(2, session.GetEntityTracker().Modified.Count, "and the records are still pending, for the caller to fix");
  }
}
