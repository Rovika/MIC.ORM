namespace Mic.Modeling.Db;

/// <summary>Represents a view, or a materialized view.</summary>
public class DbViewDef : ModelObject {
  public DbViewDef(string name, string schema) : base(name) {
    Schema = schema;
  }

  public string Schema { get; }

  /// <summary>Gets or sets a value that indicates whether the server stores the result rather than the query.</summary>
  /// <remarks>PostgreSQL and Oracle have materialized views; MySQL and SQLite do not, and SQL Server's indexed view
  /// is a different thing.</remarks>
  public bool IsMaterialized { get; set; }

  /// <summary>Gets or sets the columns the view presents, which a server reports as it does for a table.</summary>
  public List<DbColumnDef> Columns { get; set; } = [];

  /// <summary>Gets or sets the defining query.</summary>
  /// <value>The query text, or <see langword="null"/> when it was not read; it is read only when
  /// <see cref="DbModelParts.SourceText"/> is asked for.</value>
  public string? Source { get; set; }

  public string? Comment { get; set; }

  public override string ToString() => $"{Schema}.{Name}" + (IsMaterialized ? " (materialized)" : "");
}

/// <summary>Represents a procedure or a function.</summary>
/// <remarks>One type covers both; they differ in what they return and in nothing else the model holds.</remarks>
public class DbRoutineDef : ModelObject {
  public DbRoutineDef(string name, string schema) : base(name) {
    Schema = schema;
  }

  public string Schema { get; }

  public DbRoutineKind RoutineKind { get; set; }

  /// <summary>Gets or sets what a function returns, as the server declares it.</summary>
  /// <value>The type, or <see langword="null"/> for a procedure.</value>
  public string? ReturnTypeSpec { get; set; }

  public List<DbParameterDef> Parameters { get; set; } = [];

  /// <summary>Gets or sets the shape of the row set the routine returns.</summary>
  /// <value>The columns; empty when the routine returns no rows, or <see langword="null"/> when the server cannot
  /// say.</value>
  /// <remarks>PostgreSQL declares the shape. SQL Server infers it, for the first result set only, and gives up on
  /// dynamic SQL. Oracle returns a cursor whose shape is not in the catalog, and MySQL does not record one.</remarks>
  public List<DbColumnDef>? ResultColumns { get; set; }

  /// <summary>Gets or sets the body.</summary>
  /// <value>The body text, or <see langword="null"/> when it was not read; it is read only when
  /// <see cref="DbModelParts.SourceText"/> is asked for.</value>
  public string? Source { get; set; }

  public string? Comment { get; set; }

  public override string ToString() => $"{RoutineKind} {Schema}.{Name}({Parameters.Count})";
}

/// <summary>Represents one parameter of a routine.</summary>
public class DbParameterDef : ModelObject {
  public DbParameterDef(string name) : base(name) { }

  /// <summary>Gets or sets the type exactly as the server declares it, as a column's type is.</summary>
  public string TypeSpec { get; set; } = "";

  public DbParameterDirection Direction { get; set; }

  /// <summary>Gets or sets the one-based position of the parameter in the signature.</summary>
  /// <remarks>Overload resolution is by position; a document's list order is not a guarantee.</remarks>
  public int Position { get; set; }

  public bool HasDefault { get; set; }

  /// <summary>Gets or sets the default, as SQL text, where the server records one.</summary>
  public string? DefaultText { get; set; }

  public override string ToString() => $"{Direction} {Name} {TypeSpec}";
}

/// <summary>Represents an Oracle package: a named container of routines.</summary>
/// <remarks>The package owns its routines, which are children in the document. It has two sources, because Oracle
/// has two objects: the specification, which is the public face, and the body, which is the implementation. They are
/// separately compiled, and a package whose body is broken still has a usable specification.</remarks>
public class DbPackageDef : ModelObject {
  public DbPackageDef(string name, string schema) : base(name) {
    Schema = schema;
  }

  public string Schema { get; }

  public List<DbRoutineDef> Routines { get; set; } = [];

  /// <summary>Gets or sets the specification's source.</summary>
  /// <value>The source text, or <see langword="null"/> when it was not read.</value>
  public string? Source { get; set; }

  /// <summary>Gets or sets the body's source.</summary>
  /// <value>The source text, or <see langword="null"/> when it was not read or there is no body.</value>
  public string? BodySource { get; set; }

  public string? Comment { get; set; }

  public override string ToString() => $"{Schema}.{Name} ({Routines.Count} routines)";
}

/// <summary>Represents a trigger, owned by the table it fires for.</summary>
public class DbTriggerDef : ModelObject {
  public DbTriggerDef(string name) : base(name) { }

  public DbTriggerTiming Timing { get; set; }

  public DbTriggerEvents Events { get; set; }

  /// <summary>Gets or sets a value that indicates whether the trigger fires once per row rather than once per
  /// statement.</summary>
  public bool IsRowLevel { get; set; }

  /// <summary>Gets or sets a value that indicates whether the server has the trigger enabled.</summary>
  /// <value><see langword="true"/> if the trigger is enabled; otherwise, <see langword="false"/>. The default is
  /// <see langword="true"/>.</value>
  /// <remarks>A disabled trigger is still present and still shown.</remarks>
  public bool IsEnabled { get; set; } = true;

  /// <summary>Gets or sets the body.</summary>
  /// <value>The body text, or <see langword="null"/> when it was not read.</value>
  public string? Source { get; set; }

  public override string ToString() => $"{Name}: {Timing} {Events}";
}

/// <summary>Represents a CHECK constraint, owned by its table.</summary>
/// <remarks>A check constrains values and names no columns of its own, so it is not among the table's keys.</remarks>
public class DbCheckConstraintDef : ModelObject {
  public DbCheckConstraintDef(string name) : base(name) { }

  /// <summary>Gets or sets the condition, as SQL text in the server's dialect.</summary>
  /// <remarks>The server commonly rewrites the text from what was written.</remarks>
  public string Expression { get; set; } = "";

  public override string ToString() => $"{Name}: {Expression}";
}

/// <summary>Represents a user-defined type.</summary>
/// <remarks><see cref="TypeKind"/> says which of the optional parts carries the meaning.</remarks>
public class DbCustomTypeDef : ModelObject {
  public DbCustomTypeDef(string name, string schema) : base(name) {
    Schema = schema;
  }

  public string Schema { get; }

  public DbCustomTypeKind TypeKind { get; set; }

  /// <summary>Gets or sets the type a domain or an alias type is built on.</summary>
  public string? BaseTypeSpec { get; set; }

  /// <summary>Gets or sets a value that indicates whether a domain admits null.</summary>
  /// <value><see langword="true"/> if the domain admits null; otherwise, <see langword="false"/>. The default is
  /// <see langword="true"/>.</value>
  public bool IsNullable { get; set; } = true;

  /// <summary>Gets or sets a domain's constraint, as SQL text.</summary>
  public string? CheckExpression { get; set; }

  /// <summary>Gets or sets the attributes of a composite or object type, or the columns of a table type.</summary>
  public List<DbColumnDef> Attributes { get; set; } = [];

  /// <summary>Gets or sets the values of an enum type, in the order the server keeps them, which is the order they
  /// sort in.</summary>
  public List<string> EnumValues { get; set; } = [];

  public string? Comment { get; set; }

  public override string ToString() => $"{TypeKind} {Schema}.{Name}";
}

/// <summary>Represents a synonym: a second name for an object, which may live elsewhere.</summary>
public class DbSynonymDef : ModelObject {
  public DbSynonymDef(string name, string schema) : base(name) {
    Schema = schema;
  }

  public string Schema { get; }

  /// <summary>Gets or sets the schema of the target object, by name.</summary>
  /// <remarks>The target may resolve to nothing in this document; a synonym for an object in another database is an
  /// ordinary case.</remarks>
  public string? TargetSchema { get; set; }

  public string TargetName { get; set; } = "";

  /// <summary>Gets or sets the database or link the target lives in, where the server records one and it is not
  /// this database.</summary>
  public string? TargetDatabase { get; set; }

  public string? Comment { get; set; }

  public override string ToString() =>
    $"{Schema}.{Name} → " + (TargetSchema == null ? TargetName : $"{TargetSchema}.{TargetName}");
}
