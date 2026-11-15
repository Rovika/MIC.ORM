namespace Mic.Modeling.Db;

/// <summary>Specifies the kind of a database object, used by a difference to say what it is about and by a browser
/// to say what it is showing.</summary>
/// <remarks>The first six kinds are the ones an upgrade compares; the rest are read from a database and shown, and
/// the diff never sees them, but for a table function and a check constraint.</remarks>
public enum DbObjectKind {
  Schema, Sequence, Table, Column, Key, RefConstraint,

  /// <summary>A function returning rows that the model declares and MIC manages, compared through the hash of its
  /// body.</summary>
  TableFunction,

  // Read from a database and shown, never diffed; a check constraint alone is diffed, by its name.
  View, MaterializedView, Routine, Package, CustomType, Trigger, Synonym, CheckConstraint, Parameter,
}

/// <summary>Specifies what a difference between two database models is.</summary>
/// <remarks>The kind is meaningful only in a direction: swap source and target and every add becomes a drop.</remarks>
public enum DbDiffKind { Add, Drop, Modify, Rename }

/// <summary>Represents one difference between two database models, with the DDL that would apply it.</summary>
/// <remarks>The entry holds what a reader needs to see the difference: the object kind, the kind of difference, the
/// object by qualified path, a description, whether applying it loses data, and the statements it produces, so that
/// the difference and its DDL are reviewed side by side.</remarks>
public sealed class DbObjectDiff {
  public DbObjectDiff(DbObjectKind objectKind, DbDiffKind diffKind, string name,
      string? description = null, bool isDestructive = false) {
    ObjectKind = objectKind;
    DiffKind = diffKind;
    Name = name;
    Description = description;
    IsDestructive = isDestructive;
  }

  public DbObjectKind ObjectKind { get; }
  public DbDiffKind DiffKind { get; }

  /// <summary>Gets the qualified path of the object, such as <c>sales.Person.Score</c>.</summary>
  /// <remarks>The path is qualified because a bare name, such as <c>Id</c>, identifies a difference only by accident.
  /// </remarks>
  public string Name { get; }

  /// <summary>Gets the human-readable description of the difference, such as "size 50 → 80" or "now nullable",
  /// written by the comparer.</summary>
  public string? Description { get; }

  /// <summary>Gets a value that indicates whether applying the difference loses data.</summary>
  /// <remarks>The value is always reported; whether the difference runs is a policy decision made with the
  /// database's kind in hand.</remarks>
  public bool IsDestructive { get; }

  /// <summary>Gets or sets the DDL the difference produces.</summary>
  /// <value>The statements; empty when the diff was taken without generating a script, or when the statements belong
  /// to something larger, such as the columns of a table being created.</value>
  public List<DdlStatement> Statements { get; set; } = [];

  public override string ToString() =>
    $"{DiffKind} {ObjectKind} '{Name}'" + (Description == null ? "" : $": {Description}");
}

/// <summary>Represents the result of comparing two database models, as a document.</summary>
public sealed class DbModelDiff {
  public List<DbObjectDiff> Items { get; set; } = [];

  /// <summary>Gets or sets the conflicts the comparison could not resolve, such as two objects claiming one old
  /// name.</summary>
  public List<string> Errors { get; set; } = [];

  public bool HasItems => Items.Count > 0;
  public bool HasErrors => Errors.Count > 0;
  public IEnumerable<DbObjectDiff> Destructive => Items.Where(d => d.IsDestructive);

  public override string ToString() =>
    $"{Items.Count} differences" + (HasErrors ? $", {Errors.Count} errors" : "");
}
