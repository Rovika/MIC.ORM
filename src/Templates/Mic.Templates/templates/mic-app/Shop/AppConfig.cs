using System.Text.Json;
using Mic.Orm.Db;

namespace Shop;

/// <summary>Represents the host's settings, read from <c>appsettings.json</c> beside the application.</summary>
public sealed class AppConfig {
  /// <summary>Gets or sets the connection string, resolved from the value in the file: the string itself,
  /// <c>env:NAME</c> for an environment variable, or <c>file:path</c> for a file holding it.</summary>
  public string ConnectionString { get; set; } = "";

  /// <summary>Loads the settings file and resolves the connection string.</summary>
  /// <param name="fileName">The name of the file, found beside the application.</param>
  /// <returns>The settings.</returns>
  public static AppConfig Load(string fileName = "appsettings.json") {
    var path = Path.Combine(AppContext.BaseDirectory, fileName);
    if (!File.Exists(path))
      throw new InvalidOperationException($"The settings file '{path}' is missing.");
    var options = new JsonSerializerOptions {
      PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
    };
    var config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), options)
      ?? throw new InvalidOperationException($"The settings file '{path}' is empty.");
    config.ConnectionString = ConnectionStringHelper.Resolve(config.ConnectionString, Path.GetDirectoryName(path));
    return config;
  }
}
