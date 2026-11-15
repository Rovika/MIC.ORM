using System.Reflection;

namespace Mic.Common;

/// <summary>Resolves a UI element's type name and nullable flag into a CLR type, searching the core types and the
/// assemblies registered with it.</summary>
/// <remarks><c>Type.GetType</c> finds only core types and assembly-qualified names, so the host registers the
/// assemblies whose types appear in metadata with <see cref="AddAssembly(Assembly)"/>. One resolver is shared by every
/// request a server handles.</remarks>
public class TypeResolver {
  readonly List<Assembly> _assemblies = new();

  /// <summary>Holds the types resolved so far, by name.</summary>
  /// <remarks>A miss is not cached, so a name is retried and resolves once its assembly is registered. The
  /// dictionary is concurrent because one resolver serves every request; <c>_assemblies</c> is written once at
  /// startup and only read afterwards.</remarks>
  readonly System.Collections.Concurrent.ConcurrentDictionary<string, Type> _cache = new(StringComparer.Ordinal);

  /// <summary>Registers an assembly whose types may be named in metadata, such as the host's business-object
  /// assembly.</summary>
  /// <param name="assembly">The assembly to register.</param>
  /// <remarks>Call at startup, before views render. Registering an assembly twice has no effect.</remarks>
  public void AddAssembly(Assembly assembly) {
    if (assembly != null && !_assemblies.Contains(assembly)) _assemblies.Add(assembly);
  }

  /// <summary>Registers an assembly by name, the form an application document carries.</summary>
  /// <param name="assemblyName">The name of the assembly.</param>
  /// <returns><see langword="true"/> if the assembly loaded; <see langword="false"/> if it was not found or could not
  /// be loaded, so that the rest of the application still loads.</returns>
  public bool AddAssembly(string assemblyName) {
    try {
      AddAssembly(Assembly.Load(new AssemblyName(assemblyName)));
      return true;
    }
    catch (Exception) {                 // not found, wrong architecture or a bad name
      return false;
    }
  }

  /// <summary>Resolves a type name to its CLR type, with no nullability applied.</summary>
  /// <param name="typeName">The fully-qualified type name.</param>
  /// <returns>The type, or <see langword="null"/> when the name is empty or does not resolve.</returns>
  /// <remarks>The core types and assembly-qualified names are tried first, then the registered assemblies in
  /// registration order.</remarks>
  public Type? ResolveBase(string? typeName) {
    if (string.IsNullOrEmpty(typeName)) return null;
    if (_cache.TryGetValue(typeName, out var cached)) return cached;

    var type = Type.GetType(typeName);
    if (type == null) {
      foreach (var asm in _assemblies) {
        type = asm.GetType(typeName);
        if (type != null) break;
      }
    }
    if (type != null) _cache[typeName] = type;   // memoize hits only
    return type;
  }

  /// <summary>Resolves a type name and nullable flag to the effective CLR type.</summary>
  /// <param name="typeName">The fully-qualified type name.</param>
  /// <param name="nullable"><see langword="true"/> to make a value type nullable; otherwise, <see langword="false"/>.
  /// </param>
  /// <returns>The type, with a nullable value type wrapped as <c>Nullable&lt;T&gt;</c>; a reference type unchanged;
  /// or <see langword="null"/> when the base type does not resolve.</returns>
  public Type? Resolve(string? typeName, bool nullable) {
    var baseType = ResolveBase(typeName);
    if (baseType == null) return null;
    if (nullable && baseType.IsValueType) return typeof(Nullable<>).MakeGenericType(baseType);
    return baseType;
  }
}
