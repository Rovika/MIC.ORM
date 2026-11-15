namespace Mic.DbTool;

/// <summary>Specifies what <see cref="EntitySourceWriter"/> writes: the names of the namespace, the application and
/// the module, which tables are taken, and the conventions applied.</summary>
public sealed class EntitySourceOptions {
  /// <summary>Gets or sets the namespace the source declares.</summary>
  public string Namespace { get; set; } = "Generated";

  /// <summary>Gets or sets the name of the generated <c>EntityApp</c> class.</summary>
  public string AppClassName { get; set; } = "DbApp";

  /// <summary>Gets or sets the name of the generated module, or <see langword="null"/> for the schema's name
  /// followed by <c>Module</c>.</summary>
  /// <remarks>With several schemas selected each module is named after its schema, this name appended.</remarks>
  public string? ModuleName { get; set; }

  /// <summary>Gets or sets the tables and views taken, as names or patterns with <c>*</c> and <c>?</c>, a schema
  /// prefix allowed as in <c>sales.Order*</c>; empty takes every table of the loaded schemas.</summary>
  public IReadOnlyList<string> Tables { get; set; } = [];

  /// <summary>Gets or sets the tables and views left out, in the form <see cref="Tables"/> takes.</summary>
  public IReadOnlyList<string> IgnoreTables { get; set; } = [];

  /// <summary>Gets or sets a value that indicates whether a referenced entity gets an <c>IList</c> member of the
  /// entities referencing it, written commented out for the reader to take or leave.</summary>
  public bool Lists { get; set; } = true;

  /// <summary>Gets or sets a value that indicates whether a single-column Guid primary key is marked <c>[Auto]</c>.
  /// </summary>
  public bool AutoGuidKeys { get; set; } = true;

  /// <summary>Gets or sets a value that indicates whether every table, column, key and foreign key is named in an
  /// attribute, so that any identifier in the file can be renamed and the mapping stands; otherwise a name is
  /// written only where it differs from what the naming policy would derive.</summary>
  public bool AddDbObjectsNames { get; set; } = true;
}
