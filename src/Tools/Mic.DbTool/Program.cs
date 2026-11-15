using Mic.Orm;
using Mic.Orm.Db;
using Mic.Orm.Db.Model.Upgrades;

namespace Mic.DbTool;

/// <summary>The entry point: <c>mic-db upgrade &lt;config&gt; [--apply] [--output path]</c> and
/// <c>mic-db entities &lt;config&gt; [--output path]</c>.</summary>
public static class Program {
  const int ExitOk = 0, ExitFailed = 1, ExitWithErrors = 2;

  public static async Task<int> Main(string[] args) {
    try {
      return await RunAsync(args);
    } catch (Exception ex) {
      WriteError(ex.Message);
      return ExitFailed;
    }
  }

  private static async Task<int> RunAsync(string[] args) {
    if (args.Length == 0 || args[0] is "-h" or "--help" or "help") {
      ShowHelp();
      return ExitOk;
    }
    var command = args[0].ToLowerInvariant();
    if (command is not ("upgrade" or "entities")) {
      WriteError($"'{args[0]}' is not a command; the commands are: upgrade, entities.");
      ShowHelp();
      return ExitFailed;
    }
    if (args.Length < 2) {
      WriteError("The config file path is missing.");
      ShowHelp();
      return ExitFailed;
    }
    var options = ParseOptions(args.Skip(2));
    var config = ToolConfig.Load(args[1]);
    if (options.GetValueOrDefault("connection") is { } connection)
      config.ConnectionString = connection;
    config.ResolveConnectionStrings();
    return command == "upgrade" ? await UpgradeAsync(config, options) : await WriteEntitiesAsync(config, options);
  }

  /// <summary>Reads the config's database and writes the C# declarations of its tables and views.</summary>
  private static async Task<int> WriteEntitiesAsync(ToolConfig config, Dictionary<string, string?> options) {
    if (string.IsNullOrWhiteSpace(config.ProviderPath))
      throw new InvalidOperationException("The entities job needs providerPath: a folder holding the provider and "
        + "its driver, such as a built application's output folder.");
    var provider = ProviderLoader.Load(config.Provider, config.ResolvePath(config.ProviderPath));
    var settings = new DbSettings(provider, config.ConnectionString, config.SchemaConnectionString);
    Console.WriteLine($"Database: {provider.ServerKind}, reading "
      + (config.Schemas.Length == 0 ? "every schema..." : $"schema(s) {string.Join(", ", config.Schemas)}..."));
    var model = await DbToolHelper.LoadModelAsync(settings, config.Schemas);
    Console.WriteLine($"  {model.Tables.Count} table(s), {model.Views.Count} view(s) read.");

    var writer = new EntitySourceWriter(model, settings, new EntitySourceOptions {
      Namespace = config.Namespace,
      AppClassName = config.AppClass ?? "DbApp",
      ModuleName = config.Module,
      Tables = config.Tables,
      IgnoreTables = config.IgnoreTables,
      Lists = config.Lists,
      AutoGuidKeys = config.AutoGuidKeys,
      AddDbObjectsNames = config.AddDbObjectsNames,
    });
    var source = writer.Write();
    foreach (var warning in writer.Warnings)
      Console.WriteLine($"  Warning: {warning}");

    var output = options.GetValueOrDefault("output") ?? config.Output ?? $"{config.AppClass ?? "DbApp"}.cs";
    var outputPath = config.ResolvePath(output);
    File.WriteAllText(outputPath, source);
    Console.WriteLine($"Source written to '{outputPath}'.");
    return ExitOk;
  }

  /// <summary>Plans the upgrade of the config's database to the application's model, writes the script, and
  /// applies it where asked.</summary>
  private static async Task<int> UpgradeAsync(ToolConfig config, Dictionary<string, string?> options) {
    if (string.IsNullOrWhiteSpace(config.AppAssembly))
      throw new InvalidOperationException("The upgrade job needs appAssembly: the assembly holding the application.");
    var assemblyPath = config.ResolvePath(config.AppAssembly);
    var (app, context) = AppLoader.Load(assemblyPath, config.AppClass);
    Console.WriteLine($"Application: {app.Name} {app.Version} ({app.Model!.Entities.Count} entities).");

    var providerFolder = config.ProviderPath != null
      ? config.ResolvePath(config.ProviderPath)
      : Path.GetDirectoryName(assemblyPath)!;
    var provider = ProviderLoader.Load(config.Provider, providerFolder, context);
    var settings = new DbSettings(provider, config.ConnectionString, config.SchemaConnectionString) {
      DropUnknownTables = config.DropUnknownTables,
      IgnoreDbObjects = [.. config.IgnoreDbObjects],
      Options = config.Options,
      UpgradeMode = DbUpgradeMode.Always,
    };
    Console.WriteLine($"Database: {provider.ServerKind}, reading it...");

    var report = await DbToolHelper.PlanUpgradeAsync(app, settings);
    foreach (var message in report.Messages)
      Console.WriteLine($"  {message.Severity}: {message.Message}");

    var defaultOutput = $"upgrade.{config.Provider.ToString().ToLowerInvariant()}.sql";
    var outputPath = config.ResolvePath(options.GetValueOrDefault("output") ?? config.Output ?? defaultOutput);
    File.WriteAllText(outputPath, DbToolHelper.FormatScript(report, settings, app));
    Console.WriteLine($"Script written to '{outputPath}'.");
    if (report.HasErrors)
      return ExitWithErrors;

    if (options.ContainsKey("apply") && report.Script is { IsEmpty: false }) {
      Console.WriteLine("Applying the script...");
      var applied = await DbToolHelper.ApplyUpgradeAsync(app, settings, outputPath);
      Console.WriteLine(applied.IsApplied
        ? $"Applied; '{outputPath}' holds the script that ran."
        : "Not applied:" + Environment.NewLine + applied.Report());
      return applied.IsApplied ? ExitOk : ExitWithErrors;
    }
    return ExitOk;
  }

  /// <summary>Reads the options after the config path: <c>--name value</c> or a bare <c>--flag</c>.</summary>
  private static Dictionary<string, string?> ParseOptions(IEnumerable<string> arguments) {
    var options = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    string? pending = null;
    foreach (var argument in arguments) {
      if (argument.StartsWith("--", StringComparison.Ordinal)) {
        pending = argument[2..];
        options[pending] = null;
      } else if (pending != null) {
        options[pending] = argument;
        pending = null;
      } else
        throw new InvalidOperationException($"'{argument}' is not an option; an option starts with --.");
    }
    return options;
  }

  private static void ShowHelp() {
    Console.WriteLine("MIC database tool");
    Console.WriteLine();
    Console.WriteLine("  mic-db upgrade <config.json> [--apply] [--output <path>] [--connection <value>]");
    Console.WriteLine("      Reads the database the config names, compares it with the application's model and writes the");
    Console.WriteLine("      DDL script that brings the database up to the model. Nothing runs unless --apply is given.");
    Console.WriteLine();
    Console.WriteLine("  mic-db entities <config.json> [--output <path>] [--connection <value>]");
    Console.WriteLine("      Reads the tables and views the config selects and writes their C# declarations: an entity");
    Console.WriteLine("      interface each, a module per schema and the application class, as one source file.");
    Console.WriteLine();
    Console.WriteLine("  The config is JSON; its schema is mic-db.schema.json beside the tool, and a sample is in Samples.");
    Console.WriteLine("  A connection string, in the config or given with --connection, is the value itself, or");
    Console.WriteLine("  env:NAME for an environment variable, or file:path for a file holding it, relative to the config.");
    Console.WriteLine("  Exit code: 0 done, 1 failed, 2 done with errors in the report.");
  }

  private static void WriteError(string message) {
    Console.ForegroundColor = ConsoleColor.Red;
    Console.Error.WriteLine("ERROR: " + message);
    Console.ResetColor();
  }
}
