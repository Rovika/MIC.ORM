namespace Mic.Orm.Model;

/// <summary>Represents a database schema, such as <c>dbo</c> or a PostgreSQL schema.</summary>
public class EntitySchemaInfo {
  public EntitySchemaInfo(string name) { Name = name; }

  public string Name { get; }

  /// <summary>Gets or sets a value that indicates whether MIC creates and manages the schema.</summary>
  /// <value><see langword="true"/> if MIC creates the schema and the upgrade manages it; <see langword="false"/> for a
  /// schema MIC attaches to but does not own. The default is <see langword="true"/>.</value>
  public bool IsManaged { get; set; } = true;
}
