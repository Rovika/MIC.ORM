using System.Reflection;
using System.Runtime.Loader;

namespace Mic.DbTool;

/// <summary>Loads an application's assembly, or a provider's, with its dependencies from its own folder, sharing
/// with the tool every assembly the tool has loaded already, the ORM core first of all.</summary>
/// <remarks>Shared by name: a type of <c>Mic.Orm</c> the application exposes must be the tool's own, since the
/// tool hands the application to the ORM it carries. Dependencies are resolved from a dependency file: the
/// assembly's own <c>.deps.json</c>, or, for a library in a host's output folder, the host's that names it. Where
/// the folder has neither, the folder itself is probed, which finds managed assemblies and no native library.
/// </remarks>
public sealed class PluginLoadContext : AssemblyLoadContext {
  readonly AssemblyDependencyResolver? _resolver;
  readonly string _folder;

  /// <summary>Initializes a new instance of the <see cref="PluginLoadContext"/> class.</summary>
  /// <param name="mainAssemblyPath">The path of the assembly whose dependencies the context resolves; its folder is
  /// probed for anything the dependency file does not name.</param>
  public PluginLoadContext(string mainAssemblyPath) : base(Path.GetFileNameWithoutExtension(mainAssemblyPath)) {
    _folder = Path.GetDirectoryName(mainAssemblyPath) ?? "";
    _resolver = FindDependencyOwner(mainAssemblyPath) is { } owner ? new AssemblyDependencyResolver(owner) : null;
  }

  /// <summary>Finds the assembly whose dependency file says what an assembly needs: the assembly itself where it
  /// has such a file, otherwise a host in its folder whose file names it.</summary>
  /// <remarks>A library built into a host's output folder has no dependency file of its own. The host's names the
  /// native libraries of the folder, which lie in subfolders by platform, where probing the folder does not look:
  /// SQLite's engine, SQL Server's network library.</remarks>
  private static string? FindDependencyOwner(string assemblyPath) {
    const string Extension = ".deps.json";
    if (File.Exists(Path.ChangeExtension(assemblyPath, Extension)))
      return assemblyPath;
    var folder = Path.GetDirectoryName(assemblyPath);
    if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
      return null;
    var fileName = Path.GetFileName(assemblyPath);
    return Directory.GetFiles(folder, "*" + Extension).Order(StringComparer.OrdinalIgnoreCase)
      .Where(file => File.ReadAllText(file).Contains(fileName, StringComparison.OrdinalIgnoreCase))
      .Select(file => file[..^Extension.Length] + ".dll")
      .FirstOrDefault(File.Exists);
  }

  /// <summary>Loads the assembly a plugin references: the tool's own where the tool has it, the plugin's otherwise.
  /// </summary>
  /// <inheritdoc/>
  protected override Assembly? Load(AssemblyName assemblyName) {
    if (Default.Assemblies.Any(a => string.Equals(a.GetName().Name, assemblyName.Name, StringComparison.OrdinalIgnoreCase)))
      return null;                                            // the tool's, shared
    var path = _resolver?.ResolveAssemblyToPath(assemblyName);
    if (path == null) {
      var probed = Path.Combine(_folder, assemblyName.Name + ".dll");
      path = File.Exists(probed) ? probed : null;
    }
    return path == null ? null : LoadFromAssemblyPath(path);
  }

  /// <inheritdoc/>
  protected override IntPtr LoadUnmanagedDll(string unmanagedDllName) {
    var path = _resolver?.ResolveUnmanagedDllToPath(unmanagedDllName);
    return path == null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
  }
}
