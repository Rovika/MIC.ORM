namespace Mic.Modeling;

/// <summary>Specifies how much a log message matters, separately from what the message is about.</summary>
public enum Severity { Info, Warning, Error }

/// <summary>Represents one log entry in flattened form: rendered, and carrying nothing that cannot be stored or
/// sent.</summary>
/// <remarks>A database command entry flattens to its rendered SQL; the structured parameter list is not carried.
/// </remarks>
public sealed class LogItem {
  public LogItem(Severity severity, DateTime time, string text, string? location = null) {
    Severity = severity;
    Time = time;
    Text = text;
    Location = location;
  }

  public Severity Severity { get; }

  public DateTime Time { get; }

  /// <summary>Gets the rendered text of the entry.</summary>
  public string Text { get; }

  /// <summary>Gets what the entry is about: an entity and member for a model diagnostic, a qualified object path for
  /// an upgrade message.</summary>
  /// <value>The location, or <see langword="null"/> when the entry is about nothing in particular.</value>
  public string? Location { get; }

  public bool IsError => Severity == Severity.Error;

  public override string ToString() =>
    (Location == null ? "" : Location + ": ") + Text;
}
