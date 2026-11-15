namespace Mic.Modeling;

/// <summary>Represents the application document: what an application is, above any one model or view.</summary>
/// <remarks>The client loads this document first. It does not list the application's models, extensions, views or
/// languages; those are served by their own endpoints. It holds only what is true of the application as a whole.
/// </remarks>
public sealed class MicAppDef {
  /// <summary>Gets or sets the application's identifier, which is also its folder.</summary>
  /// <remarks>Renaming an application renames everything that addresses it.</remarks>
  public string Name { get; set; } = "";

  /// <summary>Gets or sets the human-facing title, shown on the app designer's General page.</summary>
  public string Title { get; set; } = "";
  public string? Description { get; set; }

  /// <summary>Gets or sets the name of the application's data model, which the server needs before it can read
  /// anything typed against the model, such as a view's parameters.</summary>
  public string ModelName { get; set; } = "";

  /// <summary>Gets or sets the names of the external assemblies whose types the application's metadata may name,
  /// which are enums: the one kind of type a model document cannot define for itself.</summary>
  /// <remarks>The values are assembly names, not paths; the type resolver loads them. An application authored
  /// entirely in the designer can take its enums from a library someone else builds.</remarks>
  public List<string> ExternalAssemblies { get; set; } = new();
}
