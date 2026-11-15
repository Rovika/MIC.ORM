using Mic.Orm.Db;
using Mic.Orm.Runtime;

namespace Mic.Orm;

/// <summary>Represents the method that handles a session event of <see cref="EntityAppEvents"/>.</summary>
/// <param name="session">The session the event concerns.</param>
public delegate void SessionEventHandler(EntitySession session);

/// <summary>Represents the method that handles the <see cref="EntityAppEvents.CommandExecuted"/> event.</summary>
/// <param name="session">The session the statement ran for.</param>
/// <param name="command">The snapshot of the statement: SQL and timings, without parameter values.</param>
public delegate void CommandEventHandler(EntitySession session, DataCommandSnapshot command);

/// <summary>Represents the method that handles the <see cref="EntityAppEvents.Error"/> event.</summary>
/// <param name="context">The operation the failure occurred in.</param>
/// <param name="error">The exception being thrown.</param>
public delegate void AppErrorEventHandler(OperationContext context, Exception error);

/// <summary>Represents the method that handles an event of the application as a whole.</summary>
/// <param name="app">The application.</param>
public delegate void AppEventHandler(EntityApp app);

/// <summary>Provides the events an application raises across its entity types, once per session or per save rather
/// than per record.</summary>
/// <remarks>The per-record events are <see cref="Model.EntityEvents"/>. Handlers are hooked at startup, from
/// <see cref="EntityModule.OnModelConstructed"/> or <see cref="IServiceInit.Init"/>, and run synchronously.</remarks>
public class EntityAppEvents {
  /// <summary>Occurs when a session has been opened for an operation.</summary>
  public event SessionEventHandler? SessionOpened;

  /// <summary>Occurs when a save begins, before anything pending has been checked or planned.</summary>
  /// <remarks>Not raised for a save with nothing pending.</remarks>
  public event SessionEventHandler? SavingChanges;

  /// <summary>Occurs when a save has succeeded: every record's changes are accepted and the pending set is empty.
  /// </summary>
  public event SessionEventHandler? SavedChanges;

  /// <summary>Occurs when a save has thrown: nothing was committed and the records are still pending.</summary>
  public event SessionEventHandler? SaveChangesAborted;

  /// <summary>Occurs for each statement a request attempted, after the request has finished with it.</summary>
  /// <remarks>On a failed request the last statement is the one that failed. Raised only for a request that has a
  /// session; a DDL request during an upgrade has none.</remarks>
  public event CommandEventHandler? CommandExecuted;

  /// <summary>Occurs when a data access failure is thrown.</summary>
  /// <remarks>For logging and paging, not handling: the exception continues on its way regardless. Not raised for a
  /// validation failure, since nothing was sent to the database.</remarks>
  public event AppErrorEventHandler? Error;

  /// <summary>Occurs when the application shuts down, before its token is cancelled and its log flushed; for the
  /// application's own services to end what they run.</summary>
  public event AppEventHandler? Shutdown;

  internal void OnSessionOpened(EntitySession session) => SessionOpened?.Invoke(session);
  internal void OnSavingChanges(EntitySession session) => SavingChanges?.Invoke(session);
  internal void OnSavedChanges(EntitySession session) => SavedChanges?.Invoke(session);
  internal void OnSaveChangesAborted(EntitySession session) => SaveChangesAborted?.Invoke(session);
  internal void OnCommandExecuted(EntitySession session, DataCommandSnapshot command) =>
    CommandExecuted?.Invoke(session, command);
  internal void OnError(OperationContext context, Exception error) => Error?.Invoke(context, error);
  internal void OnShutdown(EntityApp app) => Shutdown?.Invoke(app);
}
