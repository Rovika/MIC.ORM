namespace Mic.Modeling.Contracts;

/// <summary>Represents one response from the Mic service, the counterpart of a <see cref="MicRequest"/>.</summary>
public class MicResponse {
  public Guid Id { get; set; } = Guid.NewGuid();

  /// <summary>Gets or sets the id of the <see cref="MicRequest"/> this response answers.</summary>
  public Guid RequestId { get; set; }

  public MicStatus Status { get; set; }

  /// <summary>Gets or sets the name of what <see cref="Data"/> holds: an entity name for a data query, otherwise a
  /// content type such as "UiViewDef", "ResourcePackage" or "string".</summary>
  /// <remarks>The receiving transport resolves the name against its own allow-list to materialize
  /// <see cref="Data"/>.</remarks>
  public string DataType { get; set; } = "";

  /// <summary>Gets or sets the whole-server processing time, in milliseconds, stamped by the transport layer.</summary>
  public long TimeMs { get; set; }

  /// <summary>Gets or sets the result: a list for a query or a listing, a single object for a single document.
  /// </summary>
  /// <remarks>On the server the value is set to real objects; on the client the transport replaces what arrived
  /// with real objects before the value is read.</remarks>
  public object? Data { get; set; }

  /// <summary>Gets or sets additional information about <see cref="Data"/>. Currently unused.</summary>
  public object? AuxData { get; set; }

  /// <summary>Gets or sets the total number of matching items.</summary>
  /// <value>The count, or <see langword="null"/> when the source cannot count cheaply.</value>
  public int? TotalCount { get; set; }

  /// <summary>Gets or sets a value that indicates whether more items are available, for paged or streamed content.
  /// </summary>
  public bool HasMore { get; set; }

  /// <summary>Gets or sets the errors and warnings of the response.</summary>
  /// <remarks>A warning does not make the status <see cref="MicStatus.Failed"/>.</remarks>
  public List<MicError> Errors { get; set; } = new();
}

public enum MicStatus { Success, Failed }

public enum MicErrorKind { Error, Warning }

/// <summary>Represents one diagnostic on a response.</summary>
public class MicError {
  public MicErrorKind Kind { get; set; }
  public string Message { get; set; } = "";
}
