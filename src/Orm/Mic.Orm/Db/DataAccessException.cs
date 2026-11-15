
namespace Mic.Orm.Db;

/// <summary>The exception that is thrown when a database operation fails.</summary>
/// <remarks>The <see cref="SubType"/> classifies the failure so a caller can act on it without matching message text.
/// </remarks>
public class DataAccessException : Exception {
  public DataAccessException(string message, Exception? inner = null) : base(message, inner) { }

  /// <summary>Gets the message, followed by the <see cref="Hint"/> where there is one.</summary>
  public override string Message => Hint == null ? base.Message : base.Message + " " + Hint;

  /// <summary>Gets or sets what to do about the failure, where the provider knows, such as the statement that makes
  /// a server setting the server wants made once.</summary>
  public string? Hint { get; set; }

  /// <summary>Gets or sets the kind of failure, one of the <c>SubType</c> constants of this class.</summary>
  /// <remarks>This is a string rather than an enumeration so a failure mode no constant names can still be reported.
  /// </remarks>
  public string? SubType { get; set; }

  /// <summary>Gets or sets the alias of the database the failure is about, where several are registered.</summary>
  public string? DatabaseAlias { get; set; }

  /// <summary>Gets or sets the server's own code for the failure, a SQLSTATE such as <c>23505</c> or a vendor error
  /// number.</summary>
  public string? ProviderErrorCode { get; set; }

  /// <summary>Gets or sets the name of the constraint, key or index the failure was about, where the server says.
  /// </summary>
  public string? KeyName { get; set; }

  public string? TableName { get; set; }
  public string? ColumnName { get; set; }

  /// <summary>Gets or sets the statement that failed and the parameter values it was carrying, as text.</summary>
  /// <remarks>The same text is part of <see cref="Message"/>. Values are trimmed and followed by their types; see
  /// <see cref="DbCommandHelper.Describe(System.Data.Common.DbCommand)"/>.</remarks>
  public string? CommandSummary { get; set; }

  public const string SubTypeConnectionFailed = "ConnectionFailed";

  /// <summary>Specifies that a unique key or index rejected the row; <see cref="KeyName"/> says which.</summary>
  public const string SubTypeUniqueViolation = "UniqueViolation";

  /// <summary>Specifies that a foreign key was not satisfied, on an insert or on a delete of a row something still
  /// points at.</summary>
  public const string SubTypeIntegrityViolation = "IntegrityViolation";

  public const string SubTypeNotNullViolation = "NotNullViolation";
  public const string SubTypeCheckViolation = "CheckViolation";

  /// <summary>Specifies that the server picked the transaction as a deadlock victim, or that the lock wait ran out.
  /// </summary>
  /// <remarks>Nothing about the request was wrong; it is the one failure worth retrying.</remarks>
  public const string SubTypeDeadlock = "Deadlock";

  /// <summary>Specifies that an update or a delete found no row because the row's version no longer matched, and the
  /// save was refused.</summary>
  public const string SubTypeConcurrentUpdate = "ConcurrentUpdate";

  /// <summary>Specifies bad SQL or a reference to an object that is not there.</summary>
  public const string SubTypeSyntaxOrMissingObject = "SyntaxOrMissingObject";

  public const string SubTypeAccessDenied = "AccessDenied";

  public override string ToString() =>
    (SubType == null ? "" : $"[{SubType}] ") + base.ToString();
}
