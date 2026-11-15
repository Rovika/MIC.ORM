namespace Mic.Orm.Runtime;

/// <summary>Runs asynchronous work and blocks for its result without capturing the caller's synchronization context.
///</summary>
/// <remarks>The current synchronization context is cleared while the work starts and restored before blocking, so
/// the continuations run on pool threads and a host with a context, such as a desktop UI or Blazor Server, does not
/// deadlock. An application that itself blocks on one of the ORM's asynchronous members from such a thread is not
/// covered; the synchronous members are the ones to call there. A session's synchronous members do not come here:
/// they run under <see cref="RunMode.Sync"/>, where nothing is left to wait for. Startup and shutdown do.</remarks>
internal static class SyncOverAsyncHelper {
  public static void Run(Func<Task> start) {
    ArgumentNullException.ThrowIfNull(start);
    Start(start).GetAwaiter().GetResult();
  }

  public static TResult Run<TResult>(Func<Task<TResult>> start) {
    ArgumentNullException.ThrowIfNull(start);
    return Start(start).GetAwaiter().GetResult();
  }

  /// <summary>Starts the work with no synchronization context current and returns its task.</summary>
  internal static TTask Start<TTask>(Func<TTask> start) where TTask : Task {
    var context = SynchronizationContext.Current;
    if (context == null)
      return start();
    SynchronizationContext.SetSynchronizationContext(null);
    try {
      return start();
    } finally {
      SynchronizationContext.SetSynchronizationContext(context);
    }
  }
}
