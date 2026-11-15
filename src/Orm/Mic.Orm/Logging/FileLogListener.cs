using System.Collections.Concurrent;
using System.Text;

namespace Mic.Orm.Logging;

/// <summary>Writes log batches to a text file, for debugging and testing.</summary>
/// <remarks>The file is not rotated, size-capped, structured or shipped anywhere. Entries are grouped by
/// <see cref="LogSource"/> with a header once per group, so two concurrent sessions read as two blocks rather than a
/// strict timeline; the per-entry timestamps give the order.</remarks>
public class FileLogListener : ILogListener {
  // One lock per file path, so two listeners naming one file exclude each other and files are written in parallel.
  static readonly ConcurrentDictionary<string, object> _fileLocks = new(StringComparer.OrdinalIgnoreCase);

  readonly string _path;

  public FileLogListener(string path) {
    ArgumentException.ThrowIfNullOrWhiteSpace(path);
    _path = Path.GetFullPath(path);
    var folder = Path.GetDirectoryName(_path);
    if (!string.IsNullOrEmpty(folder))
      Directory.CreateDirectory(folder);
  }

  public string FilePath => _path;

  /// <summary>Writes an arbitrary line to the file, such as a test's separator or header.</summary>
  /// <param name="text">The line to write.</param>
  public void WriteLine(string text) => Append(text + Environment.NewLine);

  public void WriteBatch(IReadOnlyList<LogEntry> batch) {
    ArgumentNullException.ThrowIfNull(batch);
    if (batch.Count == 0)
      return;

    // One buffer for the whole batch, formatted here on the background thread.
    var text = new StringBuilder();
    var header = default(LogSource);
    var first = true;

    foreach (var entry in batch) {
      if (first || !entry.Source.Equals(header)) {
        header = entry.Source;
        first = false;
        WriteHeader(text, header);
      }
      entry.WriteText(text);
    }

    Append(text.ToString());
  }

  private static void WriteHeader(StringBuilder text, LogSource source) {
    // No header for a source with no ids, the ordinary case for a standalone application.
    if (source.SessionId == null && source.CorrelationId == null && source.WebRequestId == null)
      return;

    // A blank line before and no indent: the header is a heading, not a `--` line under a statement.
    text.AppendLine();
    text.Append("-- ");
    var wrote = false;
    if (source.SessionId is { } session) {
      text.Append("session ").Append(session.ToString("D"));
      wrote = true;
    }
    if (source.WebRequestId is { } request) {
      if (wrote) text.Append(", ");
      text.Append("request ").Append(request);
      wrote = true;
    }
    if (source.CorrelationId is { } correlation) {
      if (wrote) text.Append(", ");
      text.Append("correlation ").Append(correlation);
    }
    text.AppendLine();
  }

  /// <summary>Appends text to the file synchronously, under the file's lock.</summary>
  private void Append(string text) {
    try {
      lock (_fileLocks.GetOrAdd(_path, _ => new object()))
        File.AppendAllText(_path, text);
    } catch {
      // A log that cannot write must not break what it was logging, and there is nowhere to report this to.
    }
  }
}
