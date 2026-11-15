using System.Runtime.CompilerServices;

namespace Mic.Orm.Runtime;

/// <summary>Lets one operation at a time into a session opened with
/// <see cref="EntitySessionOptions.AddThreadSafety"/>: a second operation waits for its turn, and a call made from
/// inside the running operation passes.</summary>
/// <remarks>The operation that holds the turn leaves a token in its own asynchronous flow, so a handler it calls and
/// a request it makes itself are told from a call that came from elsewhere. A synchronous call cannot wait on a
/// thread with a synchronization context, since the running operation continues on that thread; it is refused.
/// </remarks>
internal sealed class SessionGate {
  /// <summary>Gets a value that indicates whether the caller runs inside the operation that holds the turn.</summary>
  private bool IsNested => _turn is { } turn && ReferenceEquals(FlowTurn.Value, turn);

  const string CannotWaitMessage =
    "The session is already running an operation, and this synchronous call cannot wait for it on this thread. "
    + "A session runs one operation at a time: await the first operation before starting the next, or call the "
    + "asynchronous member.";

  static readonly AsyncLocal<object?> FlowTurn = new();

  readonly SemaphoreSlim _semaphore = new(1, 1);
  volatile object? _turn;

  /// <summary>Asynchronously waits for a session's turn.</summary>
  /// <param name="gate">The session's gate, or <see langword="null"/> for a session that has none.</param>
  /// <param name="cancellation">A token that cancels the wait.</param>
  /// <returns>An awaitable whose result is the turn, to dispose when the operation ends.</returns>
  /// <remarks>The result must be awaited in the method that runs the operation: the turn is marked in that method's
  /// flow, which is what lets its nested calls pass.</remarks>
  public static TurnAwaitable EnterAsync(SessionGate? gate, CancellationToken cancellation) =>
    gate == null || gate.IsNested ? default : new TurnAwaitable(gate, gate._semaphore.WaitAsync(cancellation));

  /// <summary>Waits for the session's turn, blocking the calling thread.</summary>
  /// <param name="cancellation">A token that cancels the wait.</param>
  /// <returns>The turn, to dispose when the operation ends.</returns>
  /// <exception cref="InvalidOperationException">The session is running an operation and the calling thread has a
  /// synchronization context.</exception>
  public Turn Enter(CancellationToken cancellation) {
    if (IsNested)
      return default;
    if (!_semaphore.Wait(0, CancellationToken.None)) {
      if (SynchronizationContext.Current != null)
        throw new InvalidOperationException(CannotWaitMessage);
      _semaphore.Wait(cancellation);
    }
    return Take();
  }

  /// <summary>Marks the turn as taken by the caller's flow; the semaphore is held already.</summary>
  private Turn Take() {
    var turn = new object();
    _turn = turn;
    FlowTurn.Value = turn;
    return new Turn(this);
  }

  private void Exit() {
    _turn = null;
    FlowTurn.Value = null;
    _semaphore.Release();
  }

  /// <summary>Represents a session's turn held by an operation; disposing it ends the turn.</summary>
  /// <remarks>The default value is the turn of a nested call or of a session with no gate, and ends nothing.</remarks>
  internal readonly struct Turn(SessionGate? gate) : IDisposable {
    public void Dispose() => gate?.Exit();
  }

  /// <summary>Represents the wait for a session's turn.</summary>
  internal readonly struct TurnAwaitable(SessionGate? gate, Task? wait) {
    public TurnAwaiter GetAwaiter() => new(gate, wait);
  }

  /// <summary>Awaits a session's turn and takes it in the awaiting method's own flow.</summary>
  internal readonly struct TurnAwaiter(SessionGate? gate, Task? wait) : ICriticalNotifyCompletion {
    public bool IsCompleted => wait == null || wait.IsCompleted;

    public void OnCompleted(Action continuation) => wait!.GetAwaiter().OnCompleted(continuation);

    public void UnsafeOnCompleted(Action continuation) => wait!.GetAwaiter().UnsafeOnCompleted(continuation);

    public Turn GetResult() {
      if (gate == null)
        return default;
      wait!.GetAwaiter().GetResult();
      return gate.Take();
    }
  }
}
