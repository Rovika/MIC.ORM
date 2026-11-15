using Mic.Common;
using Mic.Orm.Runtime;

namespace Mic.Orm;

/// <summary>Provides the methods an application opens sessions and makes contexts with, and what it asks the
/// application about its model.</summary>
/// <remarks>A session comes from <see cref="OpenSession(OperationContext, EntitySessionOptions)"/> or one of its
/// companions, never from the <see cref="EntitySession"/> constructor, which is for the framework and its
/// extensions.</remarks>
public static class EntityAppExtensions {
  /// <summary>Opens a session for an anonymous user, on a new context.</summary>
  /// <param name="app">The application.</param>
  /// <param name="options">A bitwise combination of the enumeration values that specifies what the session does not
  /// do.</param>
  /// <returns>The session.</returns>
  public static IEntitySession OpenSession(this EntityApp app, EntitySessionOptions options = EntitySessionOptions.None) {
    ArgumentNullException.ThrowIfNull(app);
    return new OperationContext(app, UserInfo.Anonymous).OpenSession(options);
  }

  /// <summary>Opens a session on the context.</summary>
  /// <param name="context">The operation the session is part of.</param>
  /// <param name="options">A bitwise combination of the enumeration values that specifies what the session does not
  /// do.</param>
  /// <returns>The session.</returns>
  public static IEntitySession OpenSession(this OperationContext context,
      EntitySessionOptions options = EntitySessionOptions.None) {
    ArgumentNullException.ThrowIfNull(context);
    return new EntitySession(context, options);
  }

  /// <summary>Opens a session for the system user, on a new context.</summary>
  /// <param name="app">The application.</param>
  /// <returns>The session.</returns>
  public static IEntitySession OpenSystemSession(this EntityApp app) => app.CreateSystemContext().OpenSession();

  /// <summary>Opens a session for the system user: on this context when its user is the system user, otherwise on
  /// a child context with the system user.</summary>
  /// <param name="context">The operation the session is part of.</param>
  /// <returns>The session.</returns>
  public static IEntitySession OpenSystemSession(this OperationContext context) {
    ArgumentNullException.ThrowIfNull(context);
    return context.User.Kind == UserKind.System
      ? context.OpenSession()
      : context.CreateChildContext(UserInfo.System).OpenSession();
  }

  /// <summary>Creates a context for the system user.</summary>
  /// <param name="app">The application.</param>
  /// <returns>The context.</returns>
  public static OperationContext CreateSystemContext(this EntityApp app) {
    ArgumentNullException.ThrowIfNull(app);
    return new OperationContext(app, UserInfo.System);
  }

  /// <summary>Determines whether an entity type is in the application's model.</summary>
  /// <param name="app">The application.</param>
  /// <param name="entityType">The entity interface.</param>
  /// <returns><see langword="true"/> if the entity is in the model; otherwise, <see langword="false"/>.</returns>
  /// <remarks>For a model that varies by environment, where some builds leave an entity out.</remarks>
  public static bool IsRegisteredEntity(this EntityApp app, Type entityType) {
    ArgumentNullException.ThrowIfNull(app);
    ArgumentNullException.ThrowIfNull(entityType);
    return app.Model?.FindEntity(entityType) != null;
  }

  /// <summary>Computes the hash an <see cref="int"/> member declared with <c>[HashFor]</c> holds for a text.</summary>
  /// <param name="app">The application.</param>
  /// <param name="value">The text.</param>
  /// <returns>The 64-bit FNV-1a hash of the trimmed text, folded to 32 bits; 0 for a text that is
  /// <see langword="null"/> or blank.</returns>
  /// <remarks>The value does not depend on the .NET version or the machine, so it can be stored and compared with
  /// in a query: <c>Where(u =&gt; u.EmailHash == hash &amp;&amp; u.Email == email)</c>.</remarks>
  public static int ComputeStableHash(this EntityApp app, string? value) {
    ArgumentNullException.ThrowIfNull(app);
    return FoldHash(ComputeStableHash64(value));
  }

  /// <summary>Computes the hash a <see cref="long"/> member declared with <c>[HashFor]</c> holds for a text.</summary>
  /// <param name="app">The application.</param>
  /// <param name="value">The text.</param>
  /// <returns>The 64-bit FNV-1a hash of the trimmed text; 0 for a text that is <see langword="null"/> or blank.
  /// </returns>
  public static long ComputeStableHash64(this EntityApp app, string? value) {
    ArgumentNullException.ThrowIfNull(app);
    return ComputeStableHash64(value);
  }

  internal static long ComputeStableHash64(string? value) =>
    string.IsNullOrWhiteSpace(value) ? 0 : unchecked((long)Fnv1aHash.Compute(value.AsSpan().Trim()));

  /// <summary>Folds a 64-bit hash to 32 bits, the high half over the low.</summary>
  internal static int FoldHash(long hash) => unchecked((int)(hash ^ (hash >>> 32)));
}
