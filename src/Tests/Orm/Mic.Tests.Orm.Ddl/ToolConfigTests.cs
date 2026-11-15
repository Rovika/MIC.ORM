using Mic.DbTool;
using Mic.Orm.Db;

namespace Mic.Tests.Orm.Db;

// THE TOOL'S CONFIG, which needs no database: a connection string is the value itself, an environment variable's,
// or a file's, and what is not there is refused by name.
[TestClass]
public class ToolConfigTests {
  [TestMethod]
  public void ConnectionString_ResolvesFromValueVariableAndFile() {
    var folder = Path.Combine(Path.GetTempPath(), "mic_tests", "toolconfig_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(folder);
    var configPath = Path.Combine(folder, "job.json");
    File.WriteAllText(configPath, """
      {
        "provider": "Postgres",
        "connectionString": "env:MIC_TOOL_TEST_CONN",
        "schemaConnectionString": "file:secrets/schema.txt"
      }
      """);
    Directory.CreateDirectory(Path.Combine(folder, "secrets"));
    File.WriteAllText(Path.Combine(folder, "secrets", "schema.txt"), "  Host=schema;Database=x  \n");
    try {
      Environment.SetEnvironmentVariable("MIC_TOOL_TEST_CONN", "Host=env;Database=x");
      var config = ToolConfig.Load(configPath);
      config.ResolveConnectionStrings();
      Assert.AreEqual("Host=env;Database=x", config.ConnectionString);
      Assert.AreEqual("Host=schema;Database=x", config.SchemaConnectionString);

      // A plain value is itself; a missing variable, a missing file and an empty value are refused by name.
      Assert.AreEqual("Host=plain", config.ResolveValue("Host=plain", "connectionString"));
      var missingVariable = Assert.ThrowsExactly<InvalidOperationException>(
        () => config.ResolveValue("env:MIC_TOOL_TEST_MISSING", "connectionString"));
      StringAssert.Contains(missingVariable.Message, "MIC_TOOL_TEST_MISSING");
      var missingFile = Assert.ThrowsExactly<InvalidOperationException>(
        () => config.ResolveValue("file:secrets/none.txt", "connectionString"));
      StringAssert.Contains(missingFile.Message, "none.txt");
      Assert.ThrowsExactly<InvalidOperationException>(() => config.ResolveValue(" ", "connectionString"));
    } finally {
      Environment.SetEnvironmentVariable("MIC_TOOL_TEST_CONN", null);
      Directory.Delete(folder, recursive: true);
    }
  }

  [TestMethod]
  public void Options_ReadAsFlagNames() {
    var folder = Path.Combine(Path.GetTempPath(), "mic_tests", "toolconfig_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(folder);
    var configPath = Path.Combine(folder, "job.json");
    File.WriteAllText(configPath, """
      { "provider": "SQLite", "connectionString": "Data Source=x.db", "options": "UseRefIntegrity, AddSchemaToTableNames" }
      """);
    try {
      Assert.AreEqual(DbOptions.UseRefIntegrity | DbOptions.AddSchemaToTableNames, ToolConfig.Load(configPath).Options);
      File.WriteAllText(configPath, """{ "provider": "SQLite", "connectionString": "Data Source=x.db" }""");
      Assert.AreEqual(DbOptions.Default, ToolConfig.Load(configPath).Options, "absent is the default");
    } finally {
      Directory.Delete(folder, recursive: true);
    }
  }
}
