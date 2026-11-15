using Mic.Orm.Runtime;

namespace Mic.Tests.Orm;

// THE TURNS OF A SESSION OPENED WITH AddThreadSafety, without a server: a second operation waits for the first, a call
// from inside the running operation passes, a synchronous call waits where it can and is refused where it cannot.
[TestClass]
public class SessionGateTests {
  static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

  [TestMethod]
  public async Task SecondOperation_WaitsForTheFirst() {
    var gate = new SessionGate();
    var hold = new TaskCompletionSource();
    var steps = new List<string>();
    async Task RunAsync(string name, Task? wait) {
      using var turn = await SessionGate.EnterAsync(gate, CancellationToken.None);
      lock (steps) steps.Add(name + " in");
      if (wait != null)
        await wait;
      lock (steps) steps.Add(name + " out");
    }

    // Both started by one caller, as Task.WhenAll over two queries is: the second is not a call from inside the
    // first, and waits.
    var first = RunAsync("first", hold.Task);
    var second = RunAsync("second", null);
    Assert.IsFalse(second.IsCompleted, "the second operation ran while the first held the turn");
    hold.SetResult();
    await Task.WhenAll(first, second).WaitAsync(Limit);
    CollectionAssert.AreEqual(new[] { "first in", "first out", "second in", "second out" }, steps);

    // A session without a gate has no turns to wait for.
    using var none = await SessionGate.EnterAsync(null, CancellationToken.None);
  }

  [TestMethod]
  public async Task NestedCall_PassesAndLeavesTheTurnHeld() {
    var gate = new SessionGate();
    async Task<bool> NestAsync() {
      using var inner = await SessionGate.EnterAsync(gate, CancellationToken.None);
      await Task.Yield();
      return true;
    }

    var turn = await SessionGate.EnterAsync(gate, CancellationToken.None);
    // An internal request across an await, and a handler's synchronous call.
    Assert.IsTrue(await NestAsync().WaitAsync(Limit));
    gate.Enter(CancellationToken.None).Dispose();

    // The nested calls ended nothing: a call from elsewhere still waits, until the turn ends.
    var outside = StartElsewhere(async () => {
      using var other = await SessionGate.EnterAsync(gate, CancellationToken.None);
    });
    await Task.Delay(50);
    Assert.IsFalse(outside.IsCompleted, "a nested call ended the turn");
    turn.Dispose();
    await outside.WaitAsync(Limit);
  }

  [TestMethod]
  public async Task SynchronousCall_WaitsWhereItCanAndIsRefusedWhereItCannot() {
    var gate = new SessionGate();
    var turn = await SessionGate.EnterAsync(gate, CancellationToken.None);

    // ----- a thread with no synchronization context blocks until its turn --------------------------------------------
    var waiting = StartElsewhere(() => {
      gate.Enter(CancellationToken.None).Dispose();
      return Task.CompletedTask;
    });
    await Task.Delay(50);
    Assert.IsFalse(waiting.IsCompleted, "the synchronous call did not wait");

    // ----- a thread with one cannot wait: the running operation would continue on it ---------------------------------
    var refused = RunOnContextThread(() => gate.Enter(CancellationToken.None).Dispose());
    Assert.IsInstanceOfType<InvalidOperationException>(refused);
    StringAssert.Contains(refused.Message, "one operation at a time");

    turn.Dispose();
    await waiting.WaitAsync(Limit);
    // A free session is entered from such a thread like from any other.
    Assert.IsNull(RunOnContextThread(() => gate.Enter(CancellationToken.None).Dispose()));
  }

  [TestMethod]
  public async Task Wait_IsCancelled() {
    var gate = new SessionGate();
    using var cancellation = new CancellationTokenSource();
    var turn = await SessionGate.EnterAsync(gate, CancellationToken.None);
    var waiting = StartElsewhere(async () => {
      using var other = await SessionGate.EnterAsync(gate, cancellation.Token);
    });
    cancellation.Cancel();
    await Assert.ThrowsAsync<OperationCanceledException>(() => waiting.WaitAsync(Limit));

    // The cancelled wait took nothing: the turn ends once, and the next one is free.
    turn.Dispose();
    await StartElsewhere(async () => {
      using var next = await SessionGate.EnterAsync(gate, CancellationToken.None);
    }).WaitAsync(Limit);
  }

  /// <summary>Starts work on a pool thread outside the caller's asynchronous flow, as a call from another handler or
  /// another thread arrives.</summary>
  private static Task StartElsewhere(Func<Task> work) {
    using (ExecutionContext.SuppressFlow())
      return Task.Run(work);
  }

  /// <summary>Runs an action on a new thread that has a synchronization context, outside the caller's flow, and
  /// returns what it threw.</summary>
  private static Exception? RunOnContextThread(Action action) {
    Exception? thrown = null;
    var thread = new Thread(() => {
      SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
      try {
        action();
      } catch (Exception ex) {
        thrown = ex;
      }
    });
    thread.UnsafeStart();
    thread.Join();
    return thrown;
  }
}
