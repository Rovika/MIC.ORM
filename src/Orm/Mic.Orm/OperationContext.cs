using System.Collections.Concurrent;
using System.Linq.Expressions;
using Mic.Linq.Nodes;
using Mic.Orm.Logging;
using Mic.Orm.Model;
using Mic.Orm.Runtime;
namespace Mic.Orm;

/// <summary>Represents the context of one operation: the application, the user, the request, the values that came
/// with the request, the log, and what the operation reports back to the client.</summary>
/// <remarks>A session is opened for a context, and a context may open several sessions; each registers itself and
/// is ended, with its connection, when the context is disposed. A context is free-threaded: every member is safe to
/// call from several threads at once. A session is not; it is used by one thread at a time.</remarks>
public class OperationContext : IDisposable, IAsyncDisposable {
  /// <summary>Gets the application this operation belongs to.</summary>
  public EntityApp App { get; }

  /// <summary>Gets the model this operation works through.</summary>
  public EntityAppModel Model => App.Model!;

  /// <summary>Gets or sets the user this operation is for.</summary>
  /// <value>The user. Never <see langword="null"/>; an operation that names none has <see cref="UserInfo.Anonymous"/>.
  /// </value>
  public UserInfo User { get; set; }

  /// <summary>Gets or sets the name of the user's culture, for messages and formats.</summary>
  public string UserCulture { get; set; } = "en-US";

  /// <summary>Gets or sets the user session this operation belongs to, as the host keeps it.</summary>
  /// <value>The user session, or <see langword="null"/> where the host keeps none.</value>
  public UserSessionBase? UserSession { get; set; }

  /// <summary>Gets or sets the host's own context for the request this operation serves, such as ASP.NET's
  /// <c>HttpContext</c>.</summary>
  /// <value>The host's object, or <see langword="null"/> where there is none: a job, a test, a console.</value>
  /// <remarks>Untyped: the ORM references no web framework. The host sets it when it opens the operation and reads
  /// it back where it needs the request, the response or the caller's address.</remarks>
  public object? HttpContext { get; set; }

  /// <summary>Gets or sets the service provider of this operation's scope, such as the request's provider in a web
  /// host.</summary>
  /// <value>The provider, or <see langword="null"/> where the host gives none.</value>
  /// <remarks>The host sets it when it opens the operation. Scoped services of the host resolve through it.
  /// </remarks>
  public IServiceProvider? Services { get; set; }

  /// <summary>Gets or sets what a session of this operation does with its connection between commands.</summary>
  /// <value>One of the enumeration values that specifies the reuse mode. The default is
  /// <see cref="DbConnectionReuseMode.NoReuse"/>.</value>
  /// <remarks>A property of the operation, not of the database: the same application runs as a desktop application,
  /// a background job and a web application, and each host chooses. <see cref="DbConnectionReuseMode.KeepOpen"/> is
  /// for a host that disposes the context at the end of each request.</remarks>
  public DbConnectionReuseMode ConnectionReuseMode { get; set; }

  /// <summary>Gets or sets the name of the database this operation is for, where an application has more than one.
  /// </summary>
  /// <value>The database alias, or <see langword="null"/> for the default database.</value>
  public string? DatabaseAlias { get; set; }

  /// <summary>Gets the values the layers above wanted carried, by name.</summary>
  /// <remarks>Keys are case-insensitive. The dictionary is created on first use.</remarks>
  public IDictionary<string, object?> Values =>
    _values ??= new ConcurrentDictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

  const int SweepThreshold = 50;
  ConcurrentDictionary<string, object?>? _values;
  List<WeakReference>? _disposables;
  readonly Lock _disposablesLock = new();
  CancellationTokenSource? _cancellation;

  public OperationContext(EntityApp app, UserInfo? user = null,
      DbConnectionReuseMode connectionMode = DbConnectionReuseMode.NoReuse) {
    ArgumentNullException.ThrowIfNull(app);
    // A model that failed to build is kept for inspection but left unfinished, so an operation on one is refused here.
    if (!app.Initialized)
      throw new InvalidOperationException(
        $"Application '{app.Name}' has no usable model: Init() has either not run or did not SUCCEED. "
        + "Check EntityApp.Initialized and Report.Format() — a model with errors is left unfinished, so nothing "
        + "should be read or written through it.");
    App = app;
    User = user ?? UserInfo.Anonymous;
    ConnectionReuseMode = connectionMode;
    LogMode = app.Settings.LogMode;
  }

  /// <summary>Finds a service among the application's own, then in <see cref="Services"/>, then in the
  /// application's <see cref="EntityApp.HostServices"/>.</summary>
  /// <typeparam name="TService">The interface the service is registered under.</typeparam>
  /// <returns>The service, or <see langword="null"/> if none of the three has one under the interface.</returns>
  public TService? FindService<TService>() where TService : class =>
    App.FindOwnService<TService>()
      ?? (Services?.GetService(typeof(TService)) as TService)
      ?? (App.HostServices?.GetService(typeof(TService)) as TService);

  /// <summary>Gets a service from the application's own, then from <see cref="Services"/>, then from the
  /// application's <see cref="EntityApp.HostServices"/>.</summary>
  /// <typeparam name="TService">The interface the service is registered under.</typeparam>
  /// <returns>The service.</returns>
  /// <exception cref="InvalidOperationException">None of the three has a service under the interface.</exception>
  public TService GetService<TService>() where TService : class =>
    FindService<TService>()
      ?? throw new InvalidOperationException(
        $"Application '{App.Name}': no service is registered for '{typeof(TService).Name}'.");

  public T? GetValue<T>(string key) =>
    _values != null && _values.TryGetValue(key, out var value) && value is T typed ? typed : default;

  public bool TryGetValue(string key, out object? value) {
    if (_values != null)
      return _values.TryGetValue(key, out value);
    value = null;
    return false;
  }

  /// <summary>Sets a value, or removes it when <paramref name="value"/> is <see langword="null"/>.</summary>
  /// <param name="key">The name of the value.</param>
  /// <param name="value">The value, or <see langword="null"/> to remove it.</param>
  public void SetValue(string key, object? value) {
    if (value == null)
      _values?.TryRemove(key, out _);
    else
      Values[key] = value;
  }

  public void RemoveValue(string key) => _values?.TryRemove(key, out _);

  /// <summary>Creates a context for another user that carries this one's host context, service provider, user
  /// session, culture, connection mode, database alias, log mode and correlation id.</summary>
  /// <param name="user">The user of the child context.</param>
  /// <returns>The child context, registered to be disposed with this one.</returns>
  /// <remarks>The values in <see cref="Values"/>, the includes and the client faults are not carried.</remarks>
  public OperationContext CreateChildContext(UserInfo user) {
    ArgumentNullException.ThrowIfNull(user);
    var child = new OperationContext(App, user, ConnectionReuseMode) {
      HttpContext = HttpContext, Services = Services, UserCulture = UserCulture, UserSession = UserSession,
      DatabaseAlias = DatabaseAlias, LogMode = LogMode, CorrelationId = CorrelationId, WebRequestId = WebRequestId,
    };
    RegisterDisposable((IAsyncDisposable)child);
    return child;
  }

  // ----- includes -----------------------------------------------------------------------------------------------

  List<(LambdaExpression Selector, QueryInclude Include)>? _includes;
  readonly Lock _includesLock = new();

  /// <summary>Adds an include for every query run through a session of this context that returns the entity, and for
  /// every row of the entity an include of such a query loads.</summary>
  /// <typeparam name="TEntity">The entity interface.</typeparam>
  /// <param name="include">The member to load with the entity's rows.</param>
  /// <returns>This context.</returns>
  public OperationContext AddInclude<TEntity>(Expression<Func<TEntity, object?>> include) {
    ArgumentNullException.ThrowIfNull(include);
    lock (_includesLock)
      (_includes ??= []).Add((include, new QueryInclude(include)));
    return this;
  }

  /// <summary>Removes an include added with <see cref="AddInclude{TEntity}"/>.</summary>
  /// <typeparam name="TEntity">The entity interface.</typeparam>
  /// <param name="include">The include to remove: the same expression, or one with the same text.</param>
  /// <returns><see langword="true"/> if an include was removed; otherwise, <see langword="false"/>.</returns>
  public bool RemoveInclude<TEntity>(Expression<Func<TEntity, object?>> include) {
    ArgumentNullException.ThrowIfNull(include);
    lock (_includesLock) {
      if (_includes == null)
        return false;
      var index = _includes.FindIndex(entry => ReferenceEquals(entry.Selector, include));
      if (index < 0) {
        var text = include.ToString();
        index = _includes.FindIndex(entry => entry.Selector.ToString() == text);
      }
      if (index < 0)
        return false;
      _includes.RemoveAt(index);
      return true;
    }
  }

  public bool HasIncludes() {
    lock (_includesLock)
      return _includes is { Count: > 0 };
  }

  /// <summary>Gets the includes added to this context.</summary>
  /// <returns>A copy of the list, in the order added.</returns>
  public IReadOnlyList<LambdaExpression> GetIncludes() {
    lock (_includesLock)
      return _includes == null ? [] : [.. _includes.Select(entry => entry.Selector)];
  }

  /// <summary>Gets the includes as the query engine runs them.</summary>
  internal IReadOnlyList<QueryInclude> GetQueryIncludes() {
    lock (_includesLock)
      return _includes == null ? [] : [.. _includes.Select(entry => entry.Include)];
  }

  // ----- client faults ------------------------------------------------------------------------------------------

  ConcurrentQueue<ClientFault>? _clientFaults;

  /// <summary>Gets the client faults the operation has raised and not yet thrown.</summary>
  /// <returns>A copy of the list, in the order raised.</returns>
  public IReadOnlyList<ClientFault> ClientFaults => _clientFaults == null ? [] : [.. _clientFaults];

  /// <summary>Raises a client fault: a stop the client caused and can fix, kept until
  /// <see cref="ThrowValidation"/> throws it with the others.</summary>
  /// <param name="fault">The fault.</param>
  public void AddClientFault(ClientFault fault) {
    ArgumentNullException.ThrowIfNull(fault);
    (_clientFaults ??= new ConcurrentQueue<ClientFault>()).Enqueue(fault);
  }

  public ClientFault[] GetClientFaults() => _clientFaults == null ? [] : [.. _clientFaults];

  public bool HasClientFaults() => _clientFaults is { IsEmpty: false };

  /// <summary>Throws the client faults raised so far, if there are any, and clears them.</summary>
  /// <exception cref="ClientFaultException">At least one client fault was raised.</exception>
  public void ThrowValidation() {
    var faults = Interlocked.Exchange(ref _clientFaults, null);
    if (faults is { IsEmpty: false })
      throw new ClientFaultException([.. faults]);
  }

  // ----- the log ------------------------------------------------------------------------------------------------

  List<LogEntry>? _log;
  int _errorCount;

  /// <summary>Gets or sets whether this operation records log entries, and what happens to what it records.</summary>
  public LogMode LogMode { get; set; }

  /// <summary>Gets or sets the id that ties this operation to the ones before and after it, across processes.</summary>
  /// <remarks>Set by the host when it opens the operation, from a header of the request or from the message that
  /// started a job.</remarks>
  public string? CorrelationId { get; set; }

  /// <summary>Gets or sets the host's own id for this request, such as ASP.NET's <c>TraceIdentifier</c>.</summary>
  /// <value>The host's request id, or <see langword="null"/> where there is no host; the log then omits it.</value>
  public string? WebRequestId { get; set; }

  /// <summary>Gets a value that indicates whether an error has been logged in this operation.</summary>
  public bool HasErrors => Volatile.Read(ref _errorCount) > 0;

  /// <summary>Records a log entry, if this operation is recording at all.</summary>
  /// <param name="entry">The entry to record.</param>
  /// <remarks>An error entry posts the buffer to the log service at once, whatever the mode. At
  /// <see cref="EntityAppSettings.LogBufferSize"/>, <see cref="LogMode.Details"/> posts the buffer and
  /// <see cref="LogMode.OnError"/> drops the oldest entry.</remarks>
  public void Log(LogEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (LogMode == LogMode.None)
      return;

    var failed = entry.IsError;
    if (failed)
      Interlocked.Increment(ref _errorCount);

    // A list under a short lock rather than a concurrent queue: the batch goes out in order as one, and the
    // error-only mode drops from the front.
    lock (this) {
      _log ??= new List<LogEntry>();
      _log.Add(entry);

      // An error posts at once, whatever the mode and the buffer size, with the lead-up to it.
      if (!failed && _log.Count <= App.Settings.LogBufferSize)
        return;

      // At the cap, an error-only log has nowhere to write to yet, so it drops the oldest entry.
      if (!failed && LogMode == LogMode.OnError) {
        _log.RemoveAt(0);
        return;
      }
    }

    PostToService();
  }

  /// <summary>Records a message.</summary>
  /// <param name="message">The message to record.</param>
  public void Log(string message) => Log(new MessageLogEntry(LogSource, Now, message));

  /// <summary>Records an error, which makes an error-only operation post its log rather than discard it.</summary>
  /// <param name="message">The error message.</param>
  /// <param name="error">The exception, if there is one.</param>
  public void LogError(string message, Exception? error = null) =>
    Log(new ErrorLogEntry(LogSource, Now, message, error));

  /// <summary>Gets the source stamp of entries this context posts outside any session.</summary>
  public LogSource LogSource => new(null, CorrelationId, WebRequestId);

  internal DateTime Now => App.TimeService.UtcNow;

  /// <summary>Gets the entries recorded and not yet posted, as text, one entry per line.</summary>
  /// <returns>The text, or an empty string when nothing is buffered.</returns>
  public string GetLogContents() {
    lock (this)
      return _log == null ? "" : string.Join(Environment.NewLine, _log);
  }

  /// <summary>Posts the accumulated entries to the application's log service, or discards them.</summary>
  /// <remarks>Does not block; waiting for the listeners is <c>LogService.FlushAsync</c>. In
  /// <see cref="LogMode.OnError"/> the entries of an operation with no error are discarded.</remarks>
  public void PostToService() {
    List<LogEntry>? entries;
    lock (this) {
      entries = _log;
      _log = null;
    }
    if (entries is not { Count: > 0 })
      return;
    if (LogMode == LogMode.OnError && !HasErrors)
      return;
    App.LogService.Post(entries);
  }

  // ----- cancellation -------------------------------------------------------------------------------------------

  /// <summary>Gets the token that is cancelled when the operation is, whether by <see cref="SignalCancellation"/> or
  /// by a token given to <see cref="SetExternalCancellationToken"/>.</summary>
  public CancellationToken CancellationToken => (_cancellation ??= new CancellationTokenSource()).Token;

  public void SetExternalCancellationToken(CancellationToken token) {
    _cancellation ??= new CancellationTokenSource();
    token.Register(SignalCancellation);
  }

  /// <summary>Cancels the operation: the token is cancelled, and the next check throws.</summary>
  public void SignalCancellation() => (_cancellation ??= new CancellationTokenSource()).Cancel();

  public void ThrowIfCancelled() => _cancellation?.Token.ThrowIfCancellationRequested();

  // ----- disposables --------------------------------------------------------------------------------------------

  /// <summary>Registers an object to dispose when this operation ends: a session, or a connection kept open across
  /// calls.</summary>
  /// <param name="disposable">The object to dispose; held by a weak reference, so a registration does not keep it
  /// alive.</param>
  public void RegisterDisposable(IDisposable disposable) => Register(disposable);

  /// <summary>Registers an object to dispose asynchronously when this operation ends, such as a session.</summary>
  /// <param name="disposable">The object to dispose; held by a weak reference, so a registration does not keep it
  /// alive.</param>
  public void RegisterDisposable(IAsyncDisposable disposable) => Register(disposable);

  /// <summary>Disposes every registered object still alive, synchronously.</summary>
  /// <remarks>A session and a connection are disposed with the driver's synchronous calls. A registrant that is
  /// asynchronous only is disposed by running its disposal to completion on this thread.</remarks>
  public void DisposeAll() {
    foreach (var reference in TakeDisposables()) {
      switch (reference.Target) {
        case IDisposable disposable: disposable.Dispose(); break;
        case IAsyncDisposable asyncDisposable:
          SyncOverAsyncHelper.Run(() => asyncDisposable.DisposeAsync().AsTask());
          break;
      }
    }
  }

  /// <summary>Asynchronously disposes every registered object still alive, in the order registered.</summary>
  public async Task DisposeAllAsync() {
    foreach (var reference in TakeDisposables()) {
      switch (reference.Target) {
        case IAsyncDisposable asyncDisposable: await asyncDisposable.DisposeAsync(); break;
        case IDisposable disposable: disposable.Dispose(); break;
      }
    }
  }

  /// <summary>Posts the log and disposes every registered object still alive.</summary>
  public void Dispose() {
    PostToService();
    DisposeAll();
    GC.SuppressFinalize(this);
  }

  /// <summary>Asynchronously posts the log and disposes every registered object still alive.</summary>
  public async ValueTask DisposeAsync() {
    PostToService();
    await DisposeAllAsync();
    GC.SuppressFinalize(this);
  }

  /// <summary>Takes the registered objects out of the registry, so that each is disposed once.</summary>
  private List<WeakReference> TakeDisposables() {
    lock (_disposablesLock) {
      var taken = _disposables ?? [];
      _disposables = null;
      return taken;
    }
  }

  /// <summary>Adds a weak reference to the registry, compacting it past <see cref="SweepThreshold"/> entries so that a
  /// long-lived context does not accumulate the dead.</summary>
  private void Register(object disposable) {
    ArgumentNullException.ThrowIfNull(disposable);
    lock (_disposablesLock) {
      _disposables ??= [];
      if (_disposables.Count >= SweepThreshold)
        _disposables.RemoveAll(reference => !reference.IsAlive);
      _disposables.Add(new WeakReference(disposable));
    }
  }

  public override string ToString() => $"Context: {User}";
}
