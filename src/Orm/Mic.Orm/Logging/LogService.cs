namespace Mic.Orm.Logging;

/// <summary>Defines a target that writes log entries somewhere: a file, an API, a test's collector.</summary>
/// <remarks>The batch is the unit, and a single entry is the degenerate case. <see cref="WriteBatch"/> is called on a
/// background thread, so a blocking write is acceptable.</remarks>
public interface ILogListener {
  void WriteBatch(IReadOnlyList<LogEntry> batch);
}

/// <summary>Defines where an operation's log entries go, and how they are flushed to the listeners.</summary>
/// <remarks>Replaceable like every other application service: a host with its own logging pipeline, or a test
/// collecting entries in a list, registers its own.</remarks>
public interface ILogService {
  /// <summary>Takes an operation's entries and owns them from then on.</summary>
  /// <param name="entries">The entries, which the caller must neither mutate nor retain after the call.</param>
  /// <remarks>An implementation may hold the list by reference rather than copying it.</remarks>
  void Post(IReadOnlyList<LogEntry> entries);

  /// <summary>Asynchronously writes everything queued and waits for the listeners to finish.</summary>
  /// <returns>A task that represents the asynchronous operation.</returns>
  Task FlushAsync();

  void AddListener(ILogListener listener);
}

/// <summary>Collects every operation's log and hands batches to the listeners.</summary>
/// <remarks>One per application. A batch is one operation's entries, of whatever size that operation was; nothing
/// re-chunks it, since only a listener knows the width it wants.</remarks>
public class LogService : ILogService {
  readonly List<IReadOnlyList<LogEntry>> _pending = [];
  readonly List<ILogListener> _listeners = [];
  readonly object _dispatchLock = new();
  int _count;

  /// <summary>Gets or sets how many entries may accumulate before a dispatch happens without being asked.</summary>
  /// <value>The number of entries. The default is 200.</value>
  /// <remarks>A latency knob, not a batch width: it says when to hand what has piled up to the listeners.</remarks>
  public int BatchSize { get; set; } = 200;

  public void AddListener(ILogListener listener) {
    ArgumentNullException.ThrowIfNull(listener);
    lock (_listeners)
      _listeners.Add(listener);
  }

  public bool HasListeners {
    get { lock (_listeners) return _listeners.Count > 0; }
  }

  /// <summary>Takes an operation's entries, whole and by reference, in one add.</summary>
  /// <param name="entries">The entries, which the caller must neither mutate nor retain after the call.</param>
  public void Post(IReadOnlyList<LogEntry> entries) {
    ArgumentNullException.ThrowIfNull(entries);
    if (entries.Count == 0)
      return;

    int pending;
    lock (_pending) {
      _pending.Add(entries);
      pending = _count += entries.Count;
    }

    if (pending >= BatchSize)
      _ = Task.Run(DispatchPending);
  }

  /// <summary>Asynchronously writes everything queued and waits for the listeners to finish.</summary>
  /// <returns>A task that represents the asynchronous operation.</returns>
  /// <remarks>An <see cref="OperationContext"/> only posts; whoever owns the end of the operation, or the process,
  /// flushes and waits.</remarks>
  public Task FlushAsync() => Task.Run(DispatchPending);

  /// <summary>Takes everything pending and hands each batch to every listener, one dispatch at a time.</summary>
  private void DispatchPending() {
    lock (_dispatchLock) {
      ILogListener[] listeners;
      lock (_listeners)
        listeners = _listeners.ToArray();

      IReadOnlyList<LogEntry>[] batches;
      lock (_pending) {
        if (_pending.Count == 0)
          return;
        // Taken and cleared whether or not anyone is listening, so batches do not pile up forever.
        batches = _pending.ToArray();
        _pending.Clear();
        _count = 0;
      }
      if (listeners.Length == 0)
        return;

      foreach (var listener in listeners)
        foreach (var batch in batches)
          try {
            listener.WriteBatch(batch);
          } catch {
            // A listener that throws must not take the others or the application down, and there is nowhere to
            // report it to.
          }
    }
  }
}
