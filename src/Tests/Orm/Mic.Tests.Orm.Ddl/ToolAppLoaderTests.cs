using System.Reflection;
using Mic.DbTool;
using Mic.Orm;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Mic.Tests.Orm.Db;

// THE TOOL'S CHECK OF THE ORM AN APPLICATION WAS BUILT AGAINST, which needs no database: the ORM's assembly version
// holds the major and minor numbers only, an application of the same two passes, and one of another minor version
// is refused with both versions named.
[TestClass]
public class ToolAppLoaderTests {
  [TestMethod]
  public void OrmVersion_IsComparedByMajorAndMinor() {
    // The build writes no patch into the assembly version, so two releases a patch apart are one version here.
    var carried = typeof(EntityApp).Assembly.GetName().Version!;
    Assert.AreEqual(0, carried.Build, $"the assembly version {carried} carries a patch");
    Assert.AreEqual(0, carried.Revision, $"the assembly version {carried} carries a revision");

    // An assembly built against the ORM in hand passes; one that references no ORM holds no application.
    AppLoader.CheckOrmVersion(typeof(ToolAppLoaderTests).Assembly);
    var unrelated = Assert.ThrowsExactly<InvalidOperationException>(
      () => AppLoader.CheckOrmVersion(typeof(object).Assembly));
    StringAssert.Contains(unrelated.Message, "holds no application");

    // An application built against the next minor version is refused.
    var next = new Version(carried.Major, carried.Minor + 1, 0, 0);
    var application = CompileAppAgainst(next);
    var refused = Assert.ThrowsExactly<InvalidOperationException>(() => AppLoader.CheckOrmVersion(application));
    StringAssert.Contains(refused.Message, $"built against Mic.Orm {next.ToString(2)} ");
    StringAssert.Contains(refused.Message, $"carries {carried.ToString(2)}");
  }

  // THE PATCH, WHICH THE ASSEMBLY VERSION DOES NOT HOLD. The application runs on the ORM the tool carries, so an
  // application built with a later patch may call what the tool's ORM lacks. The patch is read from the file
  // version of the ORM beside the application: a later one is refused, the same and an earlier one pass.
  [TestMethod]
  public void OrmPatch_LaterThanTheTools_IsRefused() {
    var file = System.Diagnostics.FileVersionInfo.GetVersionInfo(typeof(EntityApp).Assembly.Location);
    var carried = new Version(file.FileMajorPart, file.FileMinorPart, file.FileBuildPart);
    var folder = Path.Combine(Path.GetTempPath(), $"mic_tool_{Guid.NewGuid():N}");
    Directory.CreateDirectory(folder);
    var application = Path.Combine(folder, "PatchApp.dll");
    try {
      // No ORM beside the application: nothing to compare.
      AppLoader.CheckOrmPatch(application);

      var later = new Version(carried.Major, carried.Minor, carried.Build + 1);
      WriteOrm(later);
      var refused = Assert.ThrowsExactly<InvalidOperationException>(() => AppLoader.CheckOrmPatch(application));
      StringAssert.Contains(refused.Message, $"built with Mic.Orm {later} ");
      StringAssert.Contains(refused.Message, $"carries {carried}");
      StringAssert.Contains(refused.Message, "Update the tool");

      WriteOrm(carried);
      AppLoader.CheckOrmPatch(application);
      if (carried.Build > 0) {
        WriteOrm(new Version(carried.Major, carried.Minor, carried.Build - 1));
        AppLoader.CheckOrmPatch(application);
      }
    } finally {
      Directory.Delete(folder, recursive: true);
    }

    // ----- local functions
    void WriteOrm(Version fileVersion) => File.WriteAllBytes(Path.Combine(folder, "Mic.Orm.dll"),
      Compile("Mic.Orm", $$"""
        [assembly: System.Reflection.AssemblyFileVersion("{{fileVersion}}")]
        namespace Mic.Orm { public class EntityApp { } }
        """, MetadataReference.CreateFromFile(typeof(object).Assembly.Location)));
  }

  /// <summary>Compiles an application against a stand-in for the ORM of the given assembly version, and loads it.
  /// </summary>
  private static Assembly CompileAppAgainst(Version ormVersion) {
    var runtime = MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
    var orm = Compile("Mic.Orm", $$"""
      [assembly: System.Reflection.AssemblyVersion("{{ormVersion}}")]
      namespace Mic.Orm { public class EntityApp { } }
      """, runtime);
    var application = Compile($"VersionApp_{Guid.NewGuid():N}",
      "public class VersionApp : Mic.Orm.EntityApp { }", runtime, MetadataReference.CreateFromImage(orm));
    return Assembly.Load(application);
  }

  private static byte[] Compile(string assemblyName, string source, params MetadataReference[] references) {
    var compilation = CSharpCompilation.Create(assemblyName, [CSharpSyntaxTree.ParseText(source)], references,
      new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    using var output = new MemoryStream();
    // With the version resource a compiler writes, which is where a file version is read from.
    using var resources = compilation.CreateDefaultWin32Resources(versionResource: true, noManifest: true,
      manifestContents: null, iconInIcoFormat: null);
    var emitted = compilation.Emit(output, win32Resources: resources);
    Assert.IsTrue(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
    return output.ToArray();
  }
}
