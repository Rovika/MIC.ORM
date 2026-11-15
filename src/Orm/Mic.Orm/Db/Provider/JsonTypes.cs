namespace Mic.Orm.Db.Provider;

/// <summary>Represents one column of the rows of a JSON document: its name, which is the JSON property's, and its
/// database type.</summary>
/// <param name="Name">The name of the column and of the JSON property.</param>
/// <param name="Type">The database type of the column.</param>
/// <param name="IsElement"><see langword="true"/> if the column is the array's element itself, as for an array of
/// scalars; otherwise, <see langword="false"/>.</param>
public sealed record JsonRowsColumn(string Name, DbTypeInfo Type, bool IsElement = false);

/// <summary>Represents one path of a JSON document to write, with the value to write there or the removal of the
/// path.</summary>
/// <param name="Path">The path in the standard spelling, such as <c>$."a"."b"</c>.</param>
/// <param name="Steps">The path's steps, one property name each.</param>
/// <param name="Json">The value as JSON text: an object, an array, or a scalar as JSON spells it.</param>
/// <param name="Scalar">The value as a plain scalar, or <see langword="null"/> for a JSON null or a fragment.</param>
/// <param name="IsScalar"><see langword="true"/> if the value is a scalar or a JSON null; <see langword="false"/> if
/// it is an object or an array.</param>
/// <param name="IsRemoval"><see langword="true"/> to remove the path from the document, in which case the other
/// values say nothing; otherwise, <see langword="false"/>.</param>
public sealed record JsonPatch(string Path, IReadOnlyList<string> Steps, string Json, object? Scalar, bool IsScalar,
  bool IsRemoval = false);

/// <summary>Represents JSON text on its way to a parameter, so a driver that has to be told a value is JSON can be.
/// </summary>
/// <remarks>The text is always bound as a parameter, never written as a literal.</remarks>
public readonly record struct JsonText(string Text) {
  public override string ToString() => Text;
}

/// <summary>Specifies the form a value takes in the temp table the rows of a parameter are staged in.</summary>
public enum RowsTempValueForm {
  /// <summary>The value itself, adapted as a parameter is.</summary>
  Value,
  /// <summary>A Guid as the hex text the JSON spelling reads it from, on a server that stores a Guid as bytes.</summary>
  GuidText,
  /// <summary>The whole row as one JSON object, on a server whose spelling reads each row out of a document.</summary>
  RowJson,
}

/// <summary>Represents a column of the temp table the rows of a parameter are staged in: its name, its type as the
/// server declares it, and the form a value takes in it.</summary>
public sealed record JsonRowsTempColumn(string Name, string TypeSpec, RowsTempValueForm Form);
