namespace Mic.Orm;

/// <summary>Represents the user an operation is performed for.</summary>
/// <remarks>Instances are immutable; <see cref="Anonymous"/> and <see cref="System"/> are shared across the process.
/// <see cref="Kind"/> says what kind of user it is; the ids are not the way to tell.</remarks>
public class UserInfo {
  /// <summary>The user id of <see cref="System"/>.</summary>
  public static readonly Guid SystemUserId = new("00000000-0000-0000-0000-000000000001");

  /// <summary>The user id of <see cref="Anonymous"/>.</summary>
  public static readonly Guid AnonymousUserId = Guid.Empty;

  /// <summary>The user id of a user known by <see cref="AltUserId"/> alone.</summary>
  public static readonly Guid BlankUserId = new("00000000-0000-0000-0000-0000000000FF");

  /// <summary>The alternative id of a user known by <see cref="UserId"/> alone.</summary>
  public const long BlankAltUserId = -1;

  /// <summary>Represents an unauthenticated user, the default for an operation that names none.</summary>
  public static readonly UserInfo Anonymous = new(AnonymousUserId, "(anonymous)", UserKind.Anonymous, 0);

  /// <summary>Represents the framework acting on its own behalf, as in a startup task, an upgrade or a background
  /// job.</summary>
  public static readonly UserInfo System = new(SystemUserId, "(system)", UserKind.System, 1);

  public Guid UserId { get; }
  public string UserName { get; }
  public UserKind Kind { get; }

  /// <summary>Gets the alternative id, for an application whose users have integer ids.</summary>
  /// <value>The id, or <see cref="BlankAltUserId"/> for a user known by <see cref="UserId"/> alone.</value>
  public long AltUserId { get; }

  /// <summary>Gets the key of the user for a cache or a dictionary: the two ids joined.</summary>
  public string Key { get; }

  public bool IsAuthenticated => Kind != UserKind.Anonymous;

  public UserInfo(Guid userId, string userName, UserKind kind = UserKind.AuthenticatedUser)
    : this(userId, userName, kind, BlankAltUserId) { }

  /// <summary>Initializes a new instance of the <see cref="UserInfo"/> class for an authenticated user known by an
  /// integer id.</summary>
  /// <param name="altUserId">The user's integer id.</param>
  /// <param name="userName">The user's name.</param>
  public UserInfo(long altUserId, string userName)
    : this(BlankUserId, userName, UserKind.AuthenticatedUser, altUserId) { }

  public UserInfo(Guid userId, string userName, UserKind kind, long altUserId) {
    ArgumentNullException.ThrowIfNull(userName);
    UserId = userId;
    UserName = userName;
    Kind = kind;
    AltUserId = altUserId;
    Key = GetKey(userId, altUserId);
  }

  /// <summary>Creates the user of a kind: the shared <see cref="System"/> or <see cref="Anonymous"/>, or an
  /// authenticated user from the ids given.</summary>
  /// <param name="kind">The kind of user.</param>
  /// <param name="userId">The user's id, or <see langword="null"/> for a user known by the integer id.</param>
  /// <param name="altUserId">The user's integer id, or <see langword="null"/> for a user known by the Guid.</param>
  /// <param name="userName">The user's name; required for an authenticated user.</param>
  /// <returns>The user.</returns>
  /// <exception cref="ArgumentException">An authenticated user has no name or no id.</exception>
  public static UserInfo Create(UserKind kind, Guid? userId, long? altUserId, string? userName) {
    switch (kind) {
      case UserKind.System: return System;
      case UserKind.Anonymous: return Anonymous;
      default:
        if (string.IsNullOrWhiteSpace(userName))
          throw new ArgumentException("An authenticated user has a name.", nameof(userName));
        if (userId == null && altUserId == null)
          throw new ArgumentException("An authenticated user has a user id or an alternative id.", nameof(userId));
        return new UserInfo(userId ?? BlankUserId, userName, UserKind.AuthenticatedUser, altUserId ?? BlankAltUserId);
    }
  }

  /// <summary>Gets the key a user with these ids has in a cache or a dictionary.</summary>
  /// <param name="userId">The user's id.</param>
  /// <param name="altUserId">The user's integer id.</param>
  /// <returns>The two ids joined.</returns>
  public static string GetKey(Guid userId, long altUserId) => $"{userId}/{altUserId}";

  public override string ToString() => UserName;
}
