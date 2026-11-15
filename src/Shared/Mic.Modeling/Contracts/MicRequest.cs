namespace Mic.Modeling.Contracts;

/// <summary>Represents one request to the Mic service, independent of the transport that carried it.</summary>
public class MicRequest {
  /// <summary>Gets or sets the correlation id, echoed back as <see cref="MicResponse.RequestId"/>.</summary>
  public Guid Id { get; set; } = Guid.NewGuid();

  public string AppName { get; set; } = "";

  /// <summary>Gets or sets the request category: metadata or data.</summary>
  public MicCategory Category { get; set; }

  /// <summary>Gets or sets the operation: read or write.</summary>
  /// <remarks>The finer verb is in the payload.</remarks>
  public MicOperation Operation { get; set; }

  /// <summary>Gets or sets the kind of metadata object a metadata call addresses, one of the
  /// <see cref="MicMetaObjectTypes"/> values.</summary>
  public string MetaObjectType { get; set; } = "";

  /// <summary>Gets or sets the type name of <see cref="Payload"/>, which the server resolves against an allow-list.
  /// </summary>
  public string PayloadType { get; set; } = "";

  /// <summary>Gets or sets the payload: the criteria of a read, or the content of a write.</summary>
  /// <remarks>The payload is deserialized in a second pass according to <see cref="PayloadType"/>.</remarks>
  public object? Payload { get; set; }
}

public enum MicCategory { Metadata, Data }

public enum MicOperation { Read, Write }

/// <summary>Provides the metadata object kinds a request names in <see cref="MicRequest.MetaObjectType"/>.</summary>
public static class MicMetaObjectTypes {
  public const string App = "App";                       // the application itself
  public const string Model = "Model";                   // a data model
  public const string ModelExt = "ModelExt";             // a model extension package
  public const string View = "View";                     // an abstract view definition
  public const string Views = "Views";                   // the list of views the app has
  public const string Resources = "Resources";           // an app-level text pack ("System", "App", "model")
  public const string ViewResources = "ViewResources";   // a view's own text pack, named by the view
  public const string ResourceManifest = "ResourceManifest";   // which text packs exist, in every language

  /// <summary>Represents the contents of a database: its schemas, tables, views and routines, read from a live
  /// server.</summary>
  public const string DbSchema = "DbSchema";

  /// <summary>Represents the SQL text of one database object, such as a table's CREATE script, named
  /// schema-qualified in the criteria.</summary>
  /// <remarks>The text is fetched on demand, separately from <see cref="DbSchema"/>.</remarks>
  public const string DbScript = "DbScript";
}

/// <summary>Provides the payload type names a request carries in <see cref="MicRequest.PayloadType"/>.</summary>
public static class MicPayloadTypes {
  public const string MetaCriteria = nameof(MicMetaCriteria);
  public const string MetaWrite = nameof(MicMetaWrite);
}

/// <summary>Provides the actions a metadata write names in <see cref="MicMetaWrite.Action"/>. This class cannot be
/// inherited.</summary>
/// <remarks>Create and update are distinct: creating an object fails on a name collision, and updating one fails
/// when it no longer exists.</remarks>
public static class MicWriteActions {
  public const string Create = "Create";
  public const string Update = "Update";
  public const string Delete = "Delete";
}

/// <summary>Represents a metadata write: the object addressed as a read addresses it, the action, and the document
/// itself.</summary>
/// <remarks>This is for metadata only; a data write has a shape of its own.</remarks>
public class MicMetaWrite {
  public string Action { get; set; } = "";

  /// <summary>Gets or sets the name of the object, addressed as <see cref="MicMetaCriteria.Name"/> addresses it.
  /// </summary>
  public string? Name { get; set; }
  /// <summary>Gets or sets the language, for the kinds that have one.</summary>
  public string? Language { get; set; }

  /// <summary>Gets or sets the document being written.</summary>
  /// <value>The document, or <see langword="null"/> for a delete. Its type is decided by
  /// <see cref="MicRequest.MetaObjectType"/>.</value>
  public object? Content { get; set; }
}

/// <summary>Represents the criteria of a metadata read.</summary>
public class MicMetaCriteria {
  /// <summary>Gets or sets the exact name of the object.</summary>
  /// <value>The name, or <see langword="null"/> to ask for all objects of the kind.</value>
  public string? Name { get; set; }

  /// <summary>Gets or sets the language, for the kinds that have one: resources and view resources.</summary>
  public string? Language { get; set; }

  /// <summary>Gets or sets a value that indicates whether the server reads the object again rather than serving
  /// what it holds.</summary>
  /// <remarks>Only the kinds the server caches, such as a database schema, are affected; for the others the value is
  /// ignored.</remarks>
  public bool Refresh { get; set; }
}

/// <summary>Represents one text pack that exists, as listed by a resource manifest.</summary>
/// <remarks>The reference identifies the pack; it says nothing about where the pack is stored.</remarks>
public class MicResourceRef {
  /// <summary>Gets or sets the name of the view the pack belongs to.</summary>
  /// <value>The view name, or <see langword="null"/> for an app-level pack.</value>
  public string? ViewName { get; set; }

  /// <summary>Gets or sets the pack name of an app-level pack: "System", "App" or "model".</summary>
  /// <value>The pack name, or <see langword="null"/> for a view's pack.</value>
  public string? Name { get; set; }

  public string Language { get; set; } = "";
}
