using System.Diagnostics;
using System.Reflection;
using Mic.Orm;

namespace Mic.DbTool;

/// <summary>Loads an application from its assembly: the <c>EntityApp</c> class named, or the one such class the
/// assembly holds, constructed and initialized.</summary>
public static class AppLoader {
  /// <summary>Loads and initializes the application.</summary>
  /// <param name="assemblyPath">The path of the assembly.</param>
  /// <param name="className">The full name of the application's class, or <see langword="null"/> for the one
  /// <c>EntityApp</c> subclass the assembly holds.</param>
  /// <returns>The application, initialized, and the context it was loaded in, which a provider loads in too.
  /// </returns>
  /// <exception cref="InvalidOperationException">The assembly, the class or the constructor is not as wanted, the
  /// assembly was built against another major or minor version of the ORM or with a later patch of it, or the
  /// application failed to initialize.</exception>
  public static (EntityApp App, PluginLoadContext Context) Load(string assemblyPath, string? className) {
    var fullPath = Path.GetFullPath(assemblyPath);
    if (!File.Exists(fullPath))
      throw new InvalidOperationException($"Application assembly '{fullPath}' not found.");
    var context = new PluginLoadContext(fullPath);
    var assembly = context.LoadFromAssemblyPath(fullPath);
    CheckOrmVersion(assembly);
    CheckOrmPatch(fullPath);

    var appType = FindAppType(assembly, className);
    var constructor = appType.GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, [])
      ?? throw new InvalidOperationException($"'{appType.FullName}' has no parameterless constructor to be made with.");
    var app = (EntityApp)constructor.Invoke([]);
    app.Init();
    return (app, context);
  }

  /// <summary>Checks that an assembly was built against the ORM the tool carries, to the major and minor version.
  /// </summary>
  /// <param name="assembly">The application's assembly.</param>
  /// <exception cref="InvalidOperationException">The assembly does not reference the ORM, or references another
  /// major or minor version of it.</exception>
  /// <remarks>The application's types are bound to the tool's ORM, not to the one it was compiled with. The ORM's
  /// assembly version holds the major and minor numbers only, so a patch apart passes.</remarks>
  public static void CheckOrmVersion(Assembly assembly) {
    var orm = typeof(EntityApp).Assembly;
    var ormName = orm.GetName();
    var referenced = assembly.GetReferencedAssemblies().FirstOrDefault(n => n.Name == ormName.Name);
    if (referenced == null)
      throw new InvalidOperationException(
        $"'{assembly.GetName().Name}' does not reference {ormName.Name}, so it holds no application.");
    if (referenced.Version == ormName.Version)
      return;
    var carried = orm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
      ?? ormName.Version?.ToString(2);
    throw new InvalidOperationException(
      $"'{assembly.GetName().Name}' was built against {ormName.Name} {referenced.Version?.ToString(2)} and this tool "
      + $"carries {carried}; the two must be of the same major and minor version. Install the tool of the "
      + "application's version, or build the application against the tool's.");
  }

  /// <summary>Checks that the ORM an application was built with is no later patch than the one the tool carries.
  /// </summary>
  /// <param name="assemblyPath">The path of the application's assembly, whose folder holds the ORM it was built
  /// with.</param>
  /// <exception cref="InvalidOperationException">The ORM beside the application is a later patch than the tool's.
  /// </exception>
  /// <remarks>The application's types are bound to the tool's ORM. A later patch may hold a member the tool's
  /// lacks, and a call to it would fail where it is reached; an earlier patch is served by the tool's. The patch
  /// is read from the file version of the ORM's assembly beside the application, since an assembly version holds
  /// none. An application with no such file beside it is not checked.</remarks>
  public static void CheckOrmPatch(string assemblyPath) {
    ArgumentNullException.ThrowIfNull(assemblyPath);
    var orm = typeof(EntityApp).Assembly;
    var ormName = orm.GetName().Name;
    var beside = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(assemblyPath)) ?? "", ormName + ".dll");
    if (GetFileVersion(beside) is not { } built || GetFileVersion(orm.Location) is not { } carried || built <= carried)
      return;
    throw new InvalidOperationException(
      $"'{Path.GetFileNameWithoutExtension(assemblyPath)}' was built with {ormName} {built} and this tool carries "
      + $"{carried}, an earlier patch, which may lack what the application calls. Update the tool to the "
      + "application's version.");
  }

  /// <summary>Gets the major, minor and patch numbers of a file's version, or <see langword="null"/> where there
  /// is no such file or it carries no version.</summary>
  private static Version? GetFileVersion(string path) {
    if (!File.Exists(path))
      return null;
    var info = FileVersionInfo.GetVersionInfo(path);
    return info.FileVersion == null ? null : new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart);
  }

  private static Type FindAppType(Assembly assembly, string? className) {
    if (className != null)
      return assembly.GetType(className, throwOnError: false)
        ?? throw new InvalidOperationException($"Class '{className}' not found in '{assembly.GetName().Name}'.");
    var candidates = assembly.GetTypes().Where(t => !t.IsAbstract && typeof(EntityApp).IsAssignableFrom(t)).ToList();
    return candidates.Count switch {
      1 => candidates[0],
      0 => throw new InvalidOperationException($"'{assembly.GetName().Name}' holds no EntityApp class."),
      _ => throw new InvalidOperationException(
        $"'{assembly.GetName().Name}' holds {candidates.Count} EntityApp classes; name one with appClass: "
        + string.Join(", ", candidates.Select(c => c.FullName))),
    };
  }
}
