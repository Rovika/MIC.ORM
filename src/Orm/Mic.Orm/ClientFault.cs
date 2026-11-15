using Mic.Orm.Runtime;

namespace Mic.Orm;

/// <summary>Provides the codes of the faults the ORM raises from its own rules, and the common ones an application
/// raises.</summary>
public static class ClientFaultCodes {
  public const string ValueMissing = "ValueMissing";
  public const string ValueTooLong = "ValueTooLong";
  public const string ValueOutOfRange = "ValueOutOfRange";
  public const string InvalidValue = "InvalidValue";
  public const string InvalidAction = "InvalidAction";
  public const string ObjectNotFound = "ObjectNotFound";
  public const string ConcurrentUpdate = "ConcurrentUpdate";
  public const string AuthenticationRequired = "AuthenticationRequired";
  public const string JsonTypeMismatch = "JsonTypeMismatch";
}

/// <summary>Represents a stop in an operation that the client caused and the client can fix: sent back to the client
/// and shown to the user.</summary>
/// <remarks>A fault is a refused input, such as a missing value or an overlong string, or a break in the middle of a
/// process caused by what is in the database. The ORM raises the first kind from its own rules when it validates a
/// save; an application raises either kind through <see cref="OperationContext.AddClientFault"/> and throws them with
/// <see cref="OperationContext.ThrowValidation"/>.</remarks>
public class ClientFault {
  /// <summary>Gets the code that names the kind of fault, one of <see cref="ClientFaultCodes"/> or the application's
  /// own.</summary>
  public string Code { get; }

  /// <summary>Gets the message for the user.</summary>
  public string Message { get; }

  /// <summary>Gets the name of the entity member the fault is about.</summary>
  /// <value>The member name, or <see langword="null"/> for a fault about a record or an operation as a whole.</value>
  public string? MemberName { get; }

  /// <summary>Gets the record the fault is about.</summary>
  /// <value>The record, or <see langword="null"/> for a fault an application raised about the operation as a whole.
  /// </value>
  public EntityRecord? Record { get; }

  /// <summary>Gets or sets the entity the fault is about as the client knows it, such as <c>Order/123</c>, or the key
  /// the client assigned to a new entity.</summary>
  public string? EntityRef { get; set; }

  /// <summary>Gets or sets a tag for the client's own use.</summary>
  public string? Tag { get; set; }

  /// <summary>Gets the parameters of the message, for a client that formats the message itself.</summary>
  /// <remarks>Keys are case-insensitive. The dictionary is created on first use.</remarks>
  public IDictionary<string, string> Parameters =>
    _parameters ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

  Dictionary<string, string>? _parameters;

  /// <summary>Initializes a new instance of the <see cref="ClientFault"/> class for a fault an application raises
  /// about the operation.</summary>
  /// <param name="code">The code that names the kind of fault.</param>
  /// <param name="message">The message for the user.</param>
  /// <param name="memberName">The name of the entity member the fault is about, if any.</param>
  public ClientFault(string code, string message, string? memberName = null) {
    ArgumentNullException.ThrowIfNull(code);
    ArgumentNullException.ThrowIfNull(message);
    Code = code;
    Message = message;
    MemberName = memberName;
  }

  /// <summary>Initializes a new instance of the <see cref="ClientFault"/> class for a fault about a record, as the
  /// ORM's validation and an extension raise them.</summary>
  /// <param name="record">The record the fault is about.</param>
  /// <param name="code">The code that names the kind of fault.</param>
  /// <param name="memberName">The name of the entity member the fault is about, if any.</param>
  /// <param name="message">The message for the user.</param>
  public ClientFault(EntityRecord record, string code, string? memberName, string message)
      : this(code, message, memberName) {
    ArgumentNullException.ThrowIfNull(record);
    Record = record;
  }

  public override string ToString() {
    var subject = Record?.Entity.Name;
    if (MemberName != null)
      subject = subject == null ? MemberName : subject + "." + MemberName;
    return subject == null ? Message : $"{subject}: {Message}";
  }
}

/// <summary>The exception that is thrown when an operation stops on client faults.</summary>
public class ClientFaultException : Exception {
  /// <summary>Gets the faults the operation stopped on.</summary>
  public IReadOnlyList<ClientFault> Faults { get; }

  /// <summary>Initializes a new instance of the <see cref="ClientFaultException"/> class for several faults.</summary>
  /// <param name="faults">The faults.</param>
  /// <param name="message">The message that precedes the faults, or <see langword="null"/> for the default one.
  /// </param>
  public ClientFaultException(IReadOnlyList<ClientFault> faults, string? message = null)
      : base(Describe(faults, message)) {
    ArgumentNullException.ThrowIfNull(faults);
    Faults = faults;
  }

  /// <summary>Initializes a new instance of the <see cref="ClientFaultException"/> class for one fault.</summary>
  /// <param name="fault">The fault.</param>
  /// <param name="message">The message that precedes the fault, or <see langword="null"/> for the default one.
  /// </param>
  public ClientFaultException(ClientFault fault, string? message = null) : this([fault], message) { }

  /// <summary>Gets the message of every fault.</summary>
  /// <returns>The messages, in the order of the faults.</returns>
  public string[] GetMessages() => [.. Faults.Select(fault => fault.Message)];

  private static string Describe(IReadOnlyList<ClientFault> faults, string? message) {
    message ??= "The operation was refused";
    return faults.Count == 1
      ? $"{message}: {faults[0]}"
      : $"{message}, {faults.Count} faults:" + Environment.NewLine
        + string.Join(Environment.NewLine, faults.Select(fault => "  " + fault));
  }
}
