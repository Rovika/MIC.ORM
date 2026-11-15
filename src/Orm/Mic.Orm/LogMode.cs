namespace Mic.Orm;

/// <summary>Specifies whether an operation records log entries, and what becomes of what it records.</summary>
/// <remarks>A mode, not a level: in <see cref="OnError"/> every entry is still captured, and what is saved is the
/// formatting and the I/O of an operation that succeeded.</remarks>
public enum LogMode {
  /// <summary>No recording; a call to <c>Log</c> costs a field read and a return.</summary>
  None = 0,

  /// <summary>The detailed log, posted whether or not the operation succeeded.</summary>
  Details,

  /// <summary>The detailed log, posted if something went wrong and otherwise discarded.</summary>
  OnError,
}
