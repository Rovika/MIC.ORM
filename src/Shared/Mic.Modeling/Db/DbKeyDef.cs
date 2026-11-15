namespace Mic.Modeling.Db;

/// <summary>Represents one column of a key, by name, with its sort direction.</summary>
public readonly record struct DbKeyColumnDef(string Column, bool Descending = false) {
  public override string ToString() => Descending ? "-" + Column : Column;
}

/// <summary>Represents a key or index on a table: a primary key, a unique constraint, an index, or the column set
/// behind a foreign key.</summary>
/// <remarks>Every consumer treats these as one; whether a unique key is rendered as a constraint or a unique index is
/// a per-vendor decision. A key lives inside its table and names the table's columns rather than pointing at them.
/// </remarks>
public class DbKeyDef : ModelObject {
  public DbKeyDef(string name, KeyFlags flags) : base(name) {
    Flags = flags;
  }

  public KeyFlags Flags { get; set; }

  /// <summary>Gets or sets the columns, in key order.</summary>
  public List<DbKeyColumnDef> Columns { get; set; } = [];

  /// <summary>Gets or sets the names of the columns carried in the index leaf but not part of the key.</summary>
  public List<string> IncludedColumns { get; set; } = [];

  /// <summary>Gets or sets the filter of a partial index, as SQL text.</summary>
  public string? Filter { get; set; }

  /// <summary>Gets or sets the access method of the index.</summary>
  /// <remarks>The value is concrete by the time a document is produced: a server states one, and the builder
  /// resolves an unstated one to the provider's default.</remarks>
  public DbIndexType IndexType { get; set; }

  public bool IsPrimaryKey => Flags.HasFlag(KeyFlags.PrimaryKey);
  public bool IsForeignKey => Flags.HasFlag(KeyFlags.ForeignKey);
  public bool IsUnique => Flags.HasFlag(KeyFlags.Unique) || IsPrimaryKey;

  public override string ToString() => $"{Name} ({Flags}): {string.Join(", ", Columns)}";
}

/// <summary>Represents a foreign key constraint: a named pairing of this table's columns with a key on another table.
/// </summary>
/// <remarks>The column set on this side is an ordinary <see cref="DbKeyDef"/> marked as a foreign key. The far end
/// is named in full, by schema, table and key, because a table name alone is not unique and a document has no object
/// graph to resolve a partial name against.</remarks>
public class DbRefConstraintDef : ModelObject {
  public DbRefConstraintDef(string name, string fromKey, string toSchema, string toTable, string toKey)
      : base(name) {
    FromKey = fromKey;
    ToSchema = toSchema;
    ToTable = toTable;
    ToKey = toKey;
  }

  /// <summary>Gets the name of the key on this table that holds the referencing columns.</summary>
  public string FromKey { get; }

  public string ToSchema { get; }
  public string ToTable { get; }

  /// <summary>Gets the name of the referenced key, a primary or unique key on the other table.</summary>
  public string ToKey { get; }

  /// <summary>Gets or sets a value that indicates whether the constraint deletes the referencing rows when the
  /// referenced row is deleted.</summary>
  public bool CascadeDelete { get; set; }

  public override string ToString() => $"{Name}: {FromKey} → {ToSchema}.{ToTable}.{ToKey}";
}
