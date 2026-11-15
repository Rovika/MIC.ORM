namespace Mic.Orm;

/// <summary>Specifies the state of a user session.</summary>
public enum UserSessionStatus {
  Active = 1,
  Expired = 2,
  LoggedOut = 3,
}

/// <summary>Represents the user session an operation belongs to, as the host keeps it between requests.</summary>
/// <remarks>A base for the host's own session class; the ORM reads nothing from it. It is carried on
/// <see cref="OperationContext.UserSession"/>.</remarks>
public class UserSessionBase {
  public Guid SessionId { get; set; }
  public UserSessionStatus Status { get; set; } = UserSessionStatus.Active;
}
