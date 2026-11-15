using System.Reflection;
namespace Mic.Linq.ExpressionTree;

/// <summary>Holds the row factory of one type, made the first time the closed type is touched.</summary>
internal static class RowFactories<T> {
  public static readonly Func<RowValueReader, T>? Create = Build();

  private static Func<RowValueReader, T>? Build() {
    var type = typeof(T);
    if (type.IsAbstract || type.IsInterface)
      return null;

    var constructors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance);

    // An anonymous type, a record, or any type built from its constructor: the parameters carry the slot names.
    foreach (var constructor in constructors) {
      var parameters = constructor.GetParameters();
      if (parameters.Length == 0 || parameters.Any(p => string.IsNullOrEmpty(p.Name)))
        continue;
      return read => {
        var values = new object?[parameters.Length];
        for (var i = 0; i < values.Length; i++)
          if (!read(parameters[i].Name!, parameters[i].ParameterType, out values[i]))
            throw new InvalidOperationException(
              $"'{type.Name}': the row has no value named '{parameters[i].Name}' to build it from.");
        return (T)constructor.Invoke(values);
      };
    }

    // Otherwise an object initializer's type: made empty, then written into.
    var empty = constructors.FirstOrDefault(c => c.GetParameters().Length == 0);
    if (empty == null)
      return null;
    var writable = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
      .Where(p => p.CanWrite).ToArray();
    return read => {
      var instance = (T)empty.Invoke(null);
      foreach (var property in writable)
        if (read(property.Name, property.PropertyType, out var value))
          property.SetValue(instance, value);
      return instance;
    };
  }
}
