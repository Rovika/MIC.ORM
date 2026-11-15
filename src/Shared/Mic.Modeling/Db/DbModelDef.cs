namespace Mic.Modeling.Db;

/// <summary>Represents everything one database contains, as a document that travels over the wire, is stored and is
/// shown in the design surface.</summary>
/// <remarks>The document is a container with no processing, no lookups and no references to the ORM, a provider or
/// an entity model. Links inside it are by name: a key names its columns and a constraint names the key it
/// references. The ORM's <c>DbModel</c> is the runtime form of the same thing, with the links resolved.</remarks>
public class DbModelDef : ModelObject {
  public DbModelDef(string name, DbServerKind serverType) : base(name) {
    ServerType = serverType;
  }

  /// <summary>Gets or sets the server vendor the model is for.</summary>
  public DbServerKind ServerType { get; set; }

  /// <summary>Gets or sets the name of the application the database belongs to.</summary>
  /// <value>The application name, or <see langword="null"/> for a model read from a database MIC does not manage.
  /// </value>
  public string? AppName { get; set; }

  /// <summary>Gets or sets the version of the application model the database was built from.</summary>
  /// <value>The version, or <see langword="null"/> for a model read from a database MIC does not manage.</value>
  public string? Version { get; set; }

  public List<DbSchemaDef> Schemas { get; set; } = [];
  public List<DbTableDef> Tables { get; set; } = [];
  public List<DbSequenceDef> Sequences { get; set; } = [];

  // ----- what a server holds beyond tables, empty unless asked for -------------------------------------------------
  public List<DbViewDef> Views { get; set; } = [];
  public List<DbRoutineDef> Routines { get; set; } = [];
  public List<DbPackageDef> Packages { get; set; } = [];
  public List<DbCustomTypeDef> CustomTypes { get; set; } = [];
  public List<DbSynonymDef> Synonyms { get; set; } = [];

  /// <summary>Gets or sets the parts that were read into this document, so that a reader can tell an object that is
  /// absent from one that was never asked for.</summary>
  /// <value>A bitwise combination of the enumeration values that names the loaded parts. The default is
  /// <see cref="DbModelParts.Default"/>.</value>
  public DbModelParts LoadedParts { get; set; } = DbModelParts.Default;

  /// <summary>Gets or sets what MIC recorded about the database: the application, its version and when it was last
  /// upgraded.</summary>
  /// <value>The record, or <see langword="null"/> for a database MIC does not manage.</value>
  /// <remarks><see cref="AppName"/> and <see cref="Version"/> are copied from the entity model, which introspection
  /// does not have; the record is what identifies a database MIC built when it is read back.</remarks>
  public DbInfoRecord? DbInfo { get; set; }

  public override string ToString() => $"{Name} ({ServerType}): {Tables.Count} tables";
}

/// <summary>Represents a database schema.</summary>
public class DbSchemaDef : ModelObject {
  public DbSchemaDef(string name) : base(name) { }

  /// <summary>Gets or sets a value that indicates whether MIC may create and change the schema.</summary>
  /// <value><see langword="true"/> if MIC may change the schema; otherwise, <see langword="false"/>. The default is
  /// <see langword="true"/>.</value>
  public bool IsManaged { get; set; } = true;
}

/// <summary>Represents a sequence, owned by a schema.</summary>
public class DbSequenceDef : ModelObject {
  public DbSequenceDef(string name, string schema) : base(name) {
    Schema = schema;
  }

  public string Schema { get; }

  /// <summary>Gets or sets the data type of the sequence, as text.</summary>
  /// <value>The type name. The default is "long".</value>
  public string DataType { get; set; } = "long";

  public long StartValue { get; set; } = 1;
  public long Increment { get; set; } = 1;
}

/// <summary>Represents a column.</summary>
public class DbColumnDef : ModelObject {
  public DbColumnDef(string name, string typeSpec) : base(name) {
    TypeSpec = typeSpec;
  }

  /// <summary>Gets or sets the type exactly as the database states it, such as <c>varchar(50)</c>,
  /// <c>numeric(18,4)</c> or <c>geography</c>.</summary>
  /// <remarks>What cannot be inferred back from a CLR type survives here.</remarks>
  public string TypeSpec { get; set; }

  /// <summary>Gets or sets the name of the CLR type the column maps to.</summary>
  /// <value>The type name, or <see langword="null"/> for a type read from a database that no mapping claims.</value>
  public string? ClrTypeName { get; set; }

  public bool IsNullable { get; set; }
  public DbColumnFlags Flags { get; set; }

  public int Size { get; set; }
  public byte Precision { get; set; }
  public byte Scale { get; set; }

  /// <summary>Gets or sets the default expression, as SQL text.</summary>
  public string? DefaultExpression { get; set; }

  /// <summary>Gets or sets the name of the default constraint, on a server where a default is an object rather than
  /// a column property.</summary>
  /// <remarks>On SQL Server a default is a constraint dropped by its own generated name. The name arrives only by
  /// introspection.</remarks>
  public string? DefaultConstraintName { get; set; }

  /// <summary>Gets or sets the comment the database records for the column, where the server keeps one.</summary>
  public string? Comment { get; set; }

  public override string ToString() => $"{Name} {TypeSpec}" + (IsNullable ? " null" : "");
}

/// <summary>Represents a table, with everything it owns.</summary>
public class DbTableDef : ModelObject {
  public DbTableDef(string name, string schema) : base(name) {
    Schema = schema;
  }

  /// <summary>Gets the name of the schema that qualifies the table.</summary>
  public string Schema { get; }

  public List<DbColumnDef> Columns { get; set; } = [];
  public List<DbKeyDef> Keys { get; set; } = [];
  public List<DbRefConstraintDef> RefConstraints { get; set; } = [];

  /// <summary>Gets or sets the triggers on the table.</summary>
  /// <remarks>Empty unless asked for; see <see cref="DbModelDef.LoadedParts"/>.</remarks>
  public List<DbTriggerDef> Triggers { get; set; } = [];

  /// <summary>Gets or sets the CHECK constraints over the table's values.</summary>
  /// <remarks>Empty unless asked for; see <see cref="DbModelDef.LoadedParts"/>.</remarks>
  public List<DbCheckConstraintDef> CheckConstraints { get; set; } = [];

  /// <summary>Gets or sets the comment the database records for the table, where the server keeps one.</summary>
  public string? Comment { get; set; }

  public override string ToString() => $"{Schema}.{Name}";
}
