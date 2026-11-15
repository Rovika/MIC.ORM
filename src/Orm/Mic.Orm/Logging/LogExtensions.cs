using Mic.Orm.Runtime;

namespace Mic.Orm.Logging;

/// <summary>Provides the methods application code calls to put its own messages among the ORM's, in the same log as
/// the statements.</summary>
public static class LogExtensions {
  /// <summary>Records a message against a session, so that it groups with the statements the session runs.</summary>
  /// <param name="session">The session.</param>
  /// <param name="message">The message to record.</param>
  public static void LogMessage(this IEntitySession session, string message) {
    ArgumentNullException.ThrowIfNull(session);
    var impl = (EntitySession)session;
    impl.Context.Log(new MessageLogEntry(impl.LogSource, impl.Context.Now, message));
  }

  /// <summary>Records a message against an operation, belonging to no particular session.</summary>
  /// <param name="context">The operation.</param>
  /// <param name="message">The message to record.</param>
  public static void LogMessage(this OperationContext context, string message) {
    ArgumentNullException.ThrowIfNull(context);
    context.Log(message);
  }
}
