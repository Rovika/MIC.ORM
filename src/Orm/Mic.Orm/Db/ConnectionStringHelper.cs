namespace Mic.Orm.Db;

/// <summary>Resolves a connection string that may name where it is kept instead of giving its value.</summary>
/// <remarks>A config file next to a project ends up in the repository, and a connection string with a password in
/// it is what a security scan rejects. A value may therefore be <c>env:NAME</c>, the environment variable of that
/// name read when the application runs, or <c>file:path</c>, a file holding the connection string, the path
/// relative to a folder the caller names; anything else is the connection string itself.</remarks>
public static class ConnectionStringHelper {
  /// <summary>Resolves a value in one of the three forms to the connection string it stands for.</summary>
  /// <param name="value">The value as written: <c>env:NAME</c>, <c>file:path</c>, or the connection string.</param>
  /// <param name="baseFolder">The folder a relative <c>file:</c> path is taken from; the current directory when
  /// omitted.</param>
  /// <returns>The connection string.</returns>
  /// <exception cref="InvalidOperationException">The value is empty, or what it names is not there.</exception>
  public static string Resolve(string value, string? baseFolder = null) {
    ArgumentNullException.ThrowIfNull(value);
    if (value.StartsWith("env:", StringComparison.OrdinalIgnoreCase)) {
      var name = value[4..].Trim();
      var resolved = Environment.GetEnvironmentVariable(name);
      return string.IsNullOrWhiteSpace(resolved)
        ? throw new InvalidOperationException($"The environment variable '{name}' is not set.")
        : resolved;
    }
    if (value.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) {
      var path = Path.GetFullPath(Path.Combine(baseFolder ?? "", value[5..].Trim()));
      if (!File.Exists(path))
        throw new InvalidOperationException($"The file '{path}' does not exist.");
      var content = File.ReadAllText(path).Trim();
      return content.Length == 0
        ? throw new InvalidOperationException($"The file '{path}' is empty.")
        : content;
    }
    return string.IsNullOrWhiteSpace(value)
      ? throw new InvalidOperationException("The value is empty; give the connection string, env:NAME or file:path.")
      : value;
  }
}
