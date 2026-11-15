using System.Text.Json;
using System.Text.Json.Serialization;
using Mic.Modeling.Db;
using Mic.Orm;
using Mic.Orm.Db.Model;
using Mic.Orm.Db.Model.Construction;
using Mic.Orm.Db.Model.Upgrades;
using Mic.Orm.Db.Postgres;
using Mic.Orm.Model;

namespace Mic.Tests.Orm;

// The db model: what a DATABASE contains. Built by hand here, because nothing maps yet — EntityAppModel → DbModel is
// plan step 11 and waits on the provider abstraction, since what a CLR type becomes in a database is a provider's
// answer rather than a general one.
[TestClass]
public class DbModelTests {
  private static DbModel NewModel(out DbTableInfo person, out DbTableInfo address) {
    var model = new DbModel("TestDb", new PostgresProvider());
    var schema = model.GetOrAddSchema("people");

    address = new DbTableInfo("Address", schema);
    address.AddColumn(new DbColumnInfo("Id", new DbTypeRef("uuid", typeof(Guid))));
    address.AddColumn(new DbColumnInfo("City", new DbTypeRef("varchar(50)", typeof(string))) { Size = 50 });
    model.AddTable(address);

    person = new DbTableInfo("Person", schema);
    person.AddColumn(new DbColumnInfo("Id", new DbTypeRef("uuid", typeof(Guid))));
    person.AddColumn(new DbColumnInfo("Name", new DbTypeRef("varchar(80)", typeof(string))) { Size = 80 });
    person.AddColumn(new DbColumnInfo("AddressId", new DbTypeRef("uuid", typeof(Guid))));
    model.AddTable(person);
    return model;
  }

  // ----- ONE hand-built model, everything it contains and every way it is asked ------------------------------------------
  //
  // Nine methods over the same two tables. The one case that MUTATES — a second `Person` in another schema — comes
  // after the counts it would change.
  [TestMethod]
  public void DbModel_HoldsSchemasTablesColumnsForOneServer() {
    var model = NewModel(out var person, out var address);
    Assert.AreEqual(2, model.Tables.Count);
    Assert.AreEqual(1, model.Schemas.Count);
    Assert.AreSame(person, model.FindTable("people", "Person"));
    Assert.AreSame(model.FindSchema("people"), person.Schema);

    // One entity model becomes several db models — one per vendor — so a db model without its server is meaningless.
    Assert.AreEqual(DbServerKind.Postgres, model.Provider.ServerKind);

    // ----- columns are reachable both ways, and looked up case-insensitively ------------------------------------------
    //
    // Database identifiers are not case-sensitive in most servers, and the ones that fold do so differently.
    Assert.AreEqual(3, person.Columns.Count);
    Assert.AreSame(person, person.GetColumn("Name").Table);
    Assert.IsNotNull(person.FindColumn("NAME"));
    Assert.IsNull(person.FindColumn("Nope"));

    // ----- and each carries the TYPE the database declares -------------------------------------------------------------
    //
    // The field the round trip rests on: whatever cannot be inferred back from a CLR type survives verbatim.
    var name = person.GetColumn("Name");
    Assert.AreEqual("varchar(80)", name.TypeSpec);
    Assert.AreEqual(typeof(string), ((DbTypeRef)name.TypeRef).ClrType);

    // …including one no mapping claims, which is a FACT worth keeping rather than an error — exactly what an exotic
    // column looks like when a database is read.
    var exotic = new DbTableInfo("Place", model.GetOrAddSchema("public"));
    exotic.AddColumn(new DbColumnInfo("Shape", new DbTypeRef("geography")));
    model.AddTable(exotic);
    Assert.AreEqual("geography", exotic.GetColumn("Shape").TypeSpec);
    Assert.IsNull(((DbTypeRef)exotic.GetColumn("Shape").TypeRef).ClrType);

    // ----- a column belongs to ONE table, and a table name is unique WITHIN a schema ------------------------------------
    Assert.ThrowsExactly<InvalidOperationException>(() => address.AddColumn(person.GetColumn("Name")));
    Assert.ThrowsExactly<InvalidOperationException>(() => model.AddTable(new DbTableInfo("Person", person.Schema)));

    // …so the same name in ANOTHER schema is an ordinary thing, which is what schemas are for.
    var archived = new DbTableInfo("Person", model.GetOrAddSchema("archive"));
    archived.AddColumn(new DbColumnInfo("Id", new DbTypeRef("uuid", typeof(Guid))));
    model.AddTable(archived);

    Assert.AreSame(person, model.FindTable("people", "Person"));
    Assert.AreSame(archived, model.FindTable("archive", "Person"));
  }

  // ----- keys and constraints ---------------------------------------------------------------------------------------

  private static DbKeyInfo PrimaryKey(DbTableInfo table, params string[] columns) {
    var key = new DbKeyInfo($"PK_{table.Name}", table, KeyFlags.PrimaryKey);
    foreach (var column in columns)
      key.AddColumn(table.GetColumn(column));
    table.AddKey(key);
    return key;
  }

  [TestMethod]
  public void Key_CarriesColumnsAndAConstraintPairsTwo() {
    var model = NewModel(out var person, out var address);

    // ----- an index carries its direction, its includes and its filter ------------------------------------------------
    var index = new DbKeyInfo("IX_Person_Name", person, KeyFlags.Index) { Filter = "\"Name\" IS NOT NULL" };
    index.AddColumn(person.GetColumn("Name"), descending: true);
    index.AddIncludedColumn(person.GetColumn("Id"));
    person.AddKey(index);

    Assert.IsTrue(index.Columns[0].Descending);
    Assert.AreEqual("Id", index.IncludedColumns.Single().Name);
    Assert.AreEqual("\"Name\" IS NOT NULL", index.Filter);

    // ----- a primary key is found, and unique without saying so ---------------------------------------------------------
    var pk = PrimaryKey(person, "Id");
    Assert.AreSame(pk, person.PrimaryKey);
    Assert.IsTrue(pk.IsUnique);
    Assert.IsFalse(pk.IsComposite);

    // ----- and a key over ANOTHER table's columns is refused --------------------------------------------------------------
    Assert.ThrowsExactly<InvalidOperationException>(
      () => new DbKeyInfo("IX_Bad", person, KeyFlags.Index).AddColumn(address.GetColumn("City")));

    // ----- a FOREIGN KEY CONSTRAINT is its own object, pairing two keys ------------------------------------------------
    //
    // On the entity side a reference is a member, so ReferenceInfo has somewhere to live. A table has no member to hang
    // it on — and a database really does hold a named constraint, which an upgrade adds, drops and reports on.
    var addressPk = PrimaryKey(address, "Id");

    var fromKey = new DbKeyInfo("FK_Person_AddressId", person, KeyFlags.ForeignKey);
    fromKey.AddColumn(person.GetColumn("AddressId"));
    person.AddKey(fromKey);

    var constraint = new DbRefConstraintInfo("FK_Person_AddressId", fromKey, addressPk) { CascadeDelete = true };
    person.AddRefConstraint(constraint);

    Assert.AreSame(person, constraint.FromTable);
    Assert.AreSame(address, constraint.ToTable);
    Assert.IsTrue(fromKey.IsForeignKey);
    Assert.IsFalse(fromKey.Flags.HasFlag(KeyFlags.Index));   // a foreign key does not imply an index on itself
    Assert.IsTrue(constraint.CascadeDelete);

    // ----- which must start from its OWN table -------------------------------------------------------------------------
    var stray = new DbKeyInfo("FK", address, KeyFlags.ForeignKey);
    Assert.ThrowsExactly<InvalidOperationException>(
      () => person.AddRefConstraint(new DbRefConstraintInfo("FK", stray, addressPk)));
  }

  // ----- the links back, and their absence ---------------------------------------------------------------------------

  [TestMethod]
  public void IntrospectedModel_HasNoLinks() {
    // The DbFirst and migration path: a database is read and there is no application to point back at. A first-class
    // case, so everything reading these references copes with their absence.
    var model = NewModel(out var person, out _);
    Assert.IsNull(model.EntityModel);
    Assert.IsNull(person.Entity);
    Assert.IsNull(person.GetColumn("Name").Member);
    Assert.IsNull(person.Schema.EntitySchema);
  }

  [TestMethod]
  public void ADerivedModelPointsBackAtEveryLevel() {
    var entityModel = BuildEntityModel().Model!;
    var entity = entityModel.FindEntity("Widget")!;

    var model = new DbModel("Db", new PostgresProvider()) { EntityModel = entityModel };
    var schema = model.GetOrAddSchema("s");
    schema.EntitySchema = entityModel.FindSchema("s");
    var table = new DbTableInfo("Widget", schema) { Entity = entity };
    table.AddColumn(new DbColumnInfo("Id", new DbTypeRef("uuid", typeof(Guid))) { Member = entity.GetMember("Id") });
    model.AddTable(table);

    Assert.AreSame(entityModel, model.EntityModel);
    Assert.AreSame(entity, table.Entity);
    Assert.AreSame(entity.GetMember("Id"), table.GetColumn("Id").Member);

    // ----- and a DbTypeSpec travels through the ENTITY model ---------------------------------------------------------
    //
    // The carrier that makes the round trip survive a type the mapping would never choose — recorded on the MEMBER,
    // because the entity model sits in the middle of database → db model → entity model → db model → database.
    Assert.AreEqual("citext", entity.GetMember("Code").DbTypeSpec);
    Assert.IsNull(entity.GetMember("Id").DbTypeSpec);
  }

  // ----- the runtime model, projected into the form that travels ----------------------------------------------------
  //
  // The converter's ONLY exercise, and the reason it is here rather than in a file of its own: it projects the model
  // this class already builds, so what it is checked against is the same shape every other method in here asserts on.
  //
  // Deliberately NOT a round trip — there is no way back, since nothing loads a db model from a document yet. What it
  // asserts instead is that nothing is LOST on the way out, which is the half that exists.
  [TestMethod]
  public void Model_ProjectsIntoADocument() {
    var model = NewModel(out var person, out var address);
    var index = new DbKeyInfo("IX_Person_Name", person, KeyFlags.Index) { Filter = "\"Name\" IS NOT NULL" };
    index.AddColumn(person.GetColumn("Name"), descending: true);
    index.AddIncludedColumn(person.GetColumn("Id"));
    person.AddKey(index);

    var fromKey = new DbKeyInfo("FK_Person_AddressId", person, KeyFlags.ForeignKey);
    fromKey.AddColumn(person.GetColumn("AddressId"));
    person.AddKey(fromKey);
    person.AddRefConstraint(
      new DbRefConstraintInfo("FK_Person_AddressId", fromKey, PrimaryKey(address, "Id")) { CascadeDelete = true });
    model.AddSequence(new DbSequenceInfo("PersonSeq", person.Schema) { StartValue = 5, Increment = 2 });

    var def = new DbModelDefBuilder().FromModel(model);

    // ----- the shape survives -----------------------------------------------------------------------------------------
    Assert.AreEqual(DbServerKind.Postgres, def.ServerType);
    Assert.AreEqual(2, def.Tables.Count);
    var personDef = def.Tables.Single(t => t.Name == "Person");
    Assert.AreEqual("people", personDef.Schema);

    var nameDef = personDef.Columns.Single(c => c.Name == "Name");
    Assert.AreEqual("varchar(80)", nameDef.TypeSpec);
    Assert.AreEqual(80, nameDef.Size);
    Assert.AreEqual("String", nameDef.ClrTypeName);

    var sequence = def.Sequences.Single();
    Assert.AreEqual("Int64", sequence.DataType);   // a CLR Type on the model, its NAME in the document
    Assert.AreEqual(5, sequence.StartValue);

    // ----- and the links became NAMES -----------------------------------------------------------------------------------
    //
    // The tree rule: a key says which of its table's columns it is over rather than pointing at them, so one column
    // appears once in the document however many keys name it.
    var indexDef = personDef.Keys.Single(k => k.Name == "IX_Person_Name");
    Assert.AreEqual("Name", indexDef.Columns.Single().Column);
    Assert.IsTrue(indexDef.Columns.Single().Descending);
    Assert.AreEqual("Id", indexDef.IncludedColumns.Single());
    Assert.AreEqual("\"Name\" IS NOT NULL", indexDef.Filter);

    var constraintDef = personDef.RefConstraints.Single();
    Assert.AreEqual("FK_Person_AddressId", constraintDef.FromKey);
    Assert.AreEqual("people", constraintDef.ToSchema);   // the far end named in FULL, since a table name is not unique
    Assert.AreEqual("Address", constraintDef.ToTable);
    Assert.AreEqual("PK_Address", constraintDef.ToKey);
    Assert.IsTrue(constraintDef.CascadeDelete);

    // ----- an introspected model says so by having no application -------------------------------------------------------
    Assert.IsNull(def.AppName);
    Assert.IsNull(def.Version);
    Assert.IsTrue(def.Schemas.Single(s => s.Name == "people").IsManaged);

    // ----- every column FACT survives, including the ones only introspection ever supplies --------------------------------
    var scored = new DbTableInfo("Scored", person.Schema);
    scored.AddColumn(new DbColumnInfo("Id", new DbTypeRef("bigint", typeof(long))) {
      DbFlags = DbColumnFlags.Identity,
    });
    scored.AddColumn(new DbColumnInfo("Amount", new DbTypeRef("numeric(18,4)", typeof(decimal)), isNullable: true) {
      Precision = 18, Scale = 4, DefaultExpression = "0", DefaultConstraintName = "DF__Scored__Amount",
    });
    scored.AddColumn(new DbColumnInfo("Version", new DbTypeRef("bytea")) { DbFlags = DbColumnFlags.RowVersion });
    PrimaryKey(scored, "Id");
    model.AddTable(scored);

    var scoredDef = new DbModelDefBuilder().FromModel(model).Tables.Single(t => t.Name == "Scored");
    var idDef = scoredDef.Columns.Single(c => c.Name == "Id");
    Assert.AreEqual(DbColumnFlags.Identity, idDef.Flags);
    Assert.IsFalse(idDef.IsNullable);

    var amountDef = scoredDef.Columns.Single(c => c.Name == "Amount");
    Assert.IsTrue(amountDef.IsNullable);
    Assert.AreEqual(18, amountDef.Precision);
    Assert.AreEqual(4, amountDef.Scale);
    Assert.AreEqual("0", amountDef.DefaultExpression);
    Assert.AreEqual("DF__Scored__Amount", amountDef.DefaultConstraintName);

    // A type no mapping claims keeps its spec and reports no CLR name — a FACT, not a gap.
    Assert.AreEqual(DbColumnFlags.RowVersion, scoredDef.Columns.Single(c => c.Name == "Version").Flags);
    Assert.IsNull(scoredDef.Columns.Single(c => c.Name == "Version").ClrTypeName);

    var pkDef = scoredDef.Keys.Single();
    Assert.IsTrue(pkDef.IsPrimaryKey);
    Assert.IsTrue(pkDef.IsUnique);              // unique without having to say so
    Assert.AreEqual("Id", pkDef.Columns.Single().Column);
  }

  // ----- and a model that HAS an entity model behind it ----------------------------------------------------------------
  //
  // The other half of the projection, and where the four weak entity links become the scalars the document keeps: an
  // application's name and version instead of the model object, and a bool instead of the schema it came from.
  [TestMethod]
  public void Model_ProjectsAViewAndAFunctionUnderTheirOwnKind() {
    // A model built from entities holds a view and a function's rows as tables with a mark. A document lists each
    // where a model read from a server has it, and leaves a temp table out.
    var model = NewModel(out _, out _);
    var schema = model.GetOrAddSchema("people");

    var view = new DbTableInfo("PersonNames", schema) {
      View = new DbViewInfo("PersonNames", schema) { IsMaterialized = true, Source = "SELECT 1" },
      Comment = "names only",
    };
    view.AddColumn(new DbColumnInfo("Name", new DbTypeRef("varchar(80)", typeof(string))));
    model.AddTable(view);

    var function = new DbTableFunctionInfo { Body = "SELECT 2", IsManaged = true };
    function.AddParameter(new DbParameterInfo("min_rank", "integer") { Position = 1 });
    var ranked = new DbTableInfo("RankedAbove", schema) { TableFunction = function };
    ranked.AddColumn(new DbColumnInfo("Id", new DbTypeRef("uuid", typeof(Guid))));
    model.AddTable(ranked);

    var temp = new DbTableInfo("PickedIds", schema) { IsTemporary = true };
    temp.AddColumn(new DbColumnInfo("Id", new DbTypeRef("uuid", typeof(Guid))));
    model.AddTable(temp);

    var def = new DbModelDefBuilder().FromModel(model);

    CollectionAssert.AreEquivalent(new[] { "Address", "Person" }, def.Tables.Select(t => t.Name).ToList());
    var viewDef = def.Views.Single();
    Assert.AreEqual("PersonNames", viewDef.Name);
    Assert.AreEqual("people", viewDef.Schema);
    Assert.IsTrue(viewDef.IsMaterialized);
    Assert.AreEqual("SELECT 1", viewDef.Source);
    Assert.AreEqual("names only", viewDef.Comment);
    Assert.AreEqual("Name", viewDef.Columns.Single().Name);

    var routineDef = def.Routines.Single();
    Assert.AreEqual("RankedAbove", routineDef.Name);
    Assert.AreEqual(DbRoutineKind.TableFunction, routineDef.RoutineKind);
    Assert.AreEqual("SELECT 2", routineDef.Source);
    Assert.AreEqual("min_rank", routineDef.Parameters.Single().Name);
    Assert.AreEqual("Id", routineDef.ResultColumns!.Single().Name);
  }

  [TestMethod]
  public void DerivedModel_ProjectsItsApplicationAsScalars() {
    var entityModel = BuildEntityModel().Model!;
    var model = new DbModel("Db", new PostgresProvider()) { EntityModel = entityModel };
    var schema = model.GetOrAddSchema("s");
    schema.EntitySchema = entityModel.FindSchema("s");
    var table = new DbTableInfo("Widget", schema) { Entity = entityModel.FindEntity("Widget") };
    table.AddColumn(new DbColumnInfo("Id", new DbTypeRef("uuid", typeof(Guid))));
    model.AddTable(table);

    var def = new DbModelDefBuilder().FromModel(model);
    Assert.AreEqual("App", def.AppName);
    Assert.AreEqual("1.0", def.Version);
    Assert.IsTrue(def.Schemas.Single().IsManaged);

    // ----- and a schema MIC does NOT own says so, which is the only thing that link was ever asked ------------------------
    schema.EntitySchema!.IsManaged = false;
    Assert.IsFalse(new DbModelDefBuilder().FromModel(model).Schemas.Single().IsManaged);

    // The entity links themselves do NOT travel: a table names its schema, and nothing names an entity. That is the
    // mapping's job, not the model document's — and the mapping is built WITH the model, so it is asserted where the
    // build is, in `DbModelBuilderTests`. An introspected model simply has none: there is no entity model to map to,
    // which is the DbFirst path rather than a failure.
    Assert.AreEqual("s", def.Tables.Single().Schema);
    Assert.IsNull(model.EntityModelMapping, "nothing built this model from an entity model");
  }

  // A CHANGE travels with the DDL that implements it, and names its object by a PATH — every table has an `Id`, so a
  // bare name identifies a change only by accident.
  [TestMethod]
  public void ChangeSet_ProjectsWithItsStatements() {
    var model = NewModel(out var person, out _);
    var column = person.GetColumn("Name");

    var changes = new DbModelDiffInfo();
    changes.Add(new DbObjectDiffInfo(DbObjectKind.Column, DbDiffKind.Drop, column, null, "no longer in the model"));
    changes.AddError("two objects claim the old name 'Person'");

    // ----- without a script, a change travels alone — the compare-only view ---------------------------------------------
    var bare = new DbModelDefBuilder().FromDiff(changes).Items.Single();
    Assert.AreEqual("people.Person.Name", bare.Name);
    Assert.AreEqual(DbDiffKind.Drop, bare.DiffKind);
    Assert.AreEqual("no longer in the model", bare.Description);
    Assert.IsTrue(bare.IsDestructive);            // a dropped column is data nothing can put back
    Assert.AreEqual(0, bare.Statements.Count);

    // ----- and WITH one, each change carries the DDL that implements it ---------------------------------------------------
    //
    // The point of attaching them: a reader reviewing an upgrade sees the change and its SQL together, rather than two
    // lists to correlate by eye. A statement belonging to no change — the ones a fresh CREATE emits — stays out.
    var drop = changes.Items.Single();
    var script = new DbUpgradeScript(new PostgresProvider().SqlDialect);
    script.Add(DbUpgradeStep.ForDdl(DbUpgradeStage.DropColumns,
      new DdlStatement(DdlKind.DropColumn, "ALTER TABLE \"people\".\"Person\" DROP COLUMN \"Name\"", "Name"), drop));
    script.Add(DbUpgradeStep.ForDdl(DbUpgradeStage.AddTables,
      new DdlStatement(DdlKind.CreateTable, "CREATE TABLE \"people\".\"Other\" ()", "Other"), null));

    var def = new DbModelDefBuilder().FromDiff(changes, script);
    var change = def.Items.Single();
    Assert.AreEqual("ALTER TABLE \"people\".\"Person\" DROP COLUMN \"Name\"", change.Statements.Single().Text);
    Assert.AreEqual(DdlKind.DropColumn, change.Statements.Single().Kind);

    Assert.AreEqual(1, def.Errors.Count);
    Assert.IsTrue(def.HasErrors);
    Assert.AreSame(change, def.Destructive.Single());
  }

  /// <summary>What the TRANSPORT does, not what `JsonSerializer` does by default.</summary>
  /// <remarks>Both ends already agree on these — `RestClientConnector` and `MicRestService` each set them — and the
  /// two that change how a db model reads are the ones asserted below: enums go over as NAMES, and nulls are not
  /// written at all. Testing a document under default options would prove it round-trips in a way nothing uses.</remarks>
  private static readonly JsonSerializerOptions WireOptions = new() {
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = { new JsonStringEnumConverter() },
  };

  // ----- the objects a database holds beyond tables ------------------------------------------------------------------
  //
  // ONE TEST, and it asserts the thing that is easy to get wrong rather than that a property holds what was put in it:
  // these documents must survive a JSON ROUND TRIP, because the design surface reads one, edits it and sends it back.
  // A get-only collection serializes perfectly and comes back EMPTY, which no compiler catches and no one-way test
  // would either.
  [TestMethod]
  public void SchemaObjects_SurviveAJsonRoundTrip() {
    var model = NewModel(out var person, out _);
    var schema = model.FindSchema("people")!;

    var view = new DbViewInfo("ActivePeople", schema) { Source = "SELECT * FROM Person", Comment = "the live ones" };
    view.AddColumn(new DbColumnInfo("Id", new DbTypeRef("uuid", typeof(Guid))));
    model.AddView(view);

    var routine = new DbRoutineInfo("FindPerson", schema, DbRoutineKind.TableFunction) {
      ReturnTypeSpec = "TABLE",
      Source = "BEGIN ... END",
    };
    routine.AddParameter(new DbParameterInfo("name", "varchar(80)") { Position = 1, HasDefault = true });
    routine.ResultColumns = [new DbColumnInfo("Id", new DbTypeRef("uuid", typeof(Guid)))];
    model.AddRoutine(routine);

    var package = new DbPackageInfo("PersonPkg", schema) { Source = "SPEC", BodySource = "BODY" };
    package.AddRoutine(new DbRoutineInfo("Inner", schema));
    model.AddPackage(package);

    var enumType = new DbCustomTypeInfo("Colour", schema, DbCustomTypeKind.Enum);
    enumType.AddEnumValue("red");
    enumType.AddEnumValue("green");
    model.AddCustomType(enumType);

    model.AddSynonym(new DbSynonymInfo("P", schema, "Person") { TargetSchema = "other" });

    person.AddTrigger(new DbTriggerInfo("trg_audit", person) {
      Timing = DbTriggerTiming.After,
      Events = DbTriggerEvents.Insert | DbTriggerEvents.Update,
      IsRowLevel = true,
    });
    person.AddCheckConstraint(new DbCheckConstraintInfo("ck_age", person, "Age > 0"));
    model.LoadedParts = DbModelParts.All;

    // A package OWNS its routines, so the one inside it is not also loose on the model — it would be drawn twice.
    Assert.AreSame(package, package.Routines.Single().Package);
    Assert.AreEqual(1, model.Routines.Count);

    var def = new DbModelDefBuilder().FromModel(model);
    var json = JsonSerializer.Serialize(def, WireOptions);
    var back = JsonSerializer.Deserialize<DbModelDef>(json, WireOptions)!;

    // THE ENUMS TRAVEL AS TEXT, which is what makes this worth asserting rather than assuming: a document written with
    // default options proves nothing about one written the way the transport writes it.
    StringAssert.Contains(json, "\"Enum\"", "an enum member should be its NAME on the wire, not its number");
    StringAssert.Contains(json, "Insert, Update", "a multi-flag value is a list of NAMES, and has to read back as one");
    Assert.IsFalse(json.Contains("MetaData"),
      "an empty metadata bag on every object is about a megabyte of nothing in a large schema");

    Assert.AreEqual(DbModelParts.All, back.LoadedParts, "what was READ has to travel, or an empty list is ambiguous");

    var viewBack = back.Views.Single();
    Assert.AreEqual("ActivePeople", viewBack.Name);
    Assert.AreEqual("people", viewBack.Schema);
    Assert.AreEqual("SELECT * FROM Person", viewBack.Source);
    Assert.AreEqual(1, viewBack.Columns.Count, "a get-only collection would come back empty here");

    var routineBack = back.Routines.Single();
    Assert.AreEqual(DbRoutineKind.TableFunction, routineBack.RoutineKind);
    Assert.AreEqual(1, routineBack.Parameters.Count);
    Assert.IsTrue(routineBack.Parameters[0].HasDefault);
    Assert.AreEqual(1, routineBack.ResultColumns!.Count);

    // NULL IS AN ANSWER: "this server cannot say what it returns" has to survive as itself, not become "returns
    // nothing" — two of the five vendors never report a shape.
    var unknown = JsonSerializer.Deserialize<DbRoutineDef>(
      JsonSerializer.Serialize(new DbRoutineDef("P", "s"), WireOptions), WireOptions)!;
    Assert.IsNull(unknown.ResultColumns);

    Assert.AreEqual("BODY", back.Packages.Single().BodySource);
    Assert.AreEqual("Inner", back.Packages.Single().Routines.Single().Name);
    CollectionAssert.AreEqual(new[] { "red", "green" }, back.CustomTypes.Single().EnumValues);
    Assert.AreEqual("other", back.Synonyms.Single().TargetSchema);

    var tableBack = back.Tables.Single(t => t.Name == "Person");
    Assert.AreEqual(DbTriggerEvents.Insert | DbTriggerEvents.Update, tableBack.Triggers.Single().Events);
    Assert.AreEqual("Age > 0", tableBack.CheckConstraints.Single().Expression);
  }

  private interface IWidget {
    [PrimaryKey] Guid Id { get; set; }
    [Column(DbTypeSpec = "citext")] string Code { get; set; }
  }

  private static EntityApp BuildEntityModel() {
    var app = new EntityApp("App", new Version(1, 0));
    var module = new EntityModule("M", new Version(1, 0));
    module.RegisterEntities(typeof(IWidget));
    app.AddModule(module, "s");
    app.TryInit();
    Assert.IsTrue(app.Initialized, app.Report());
    return app;
  }
}
