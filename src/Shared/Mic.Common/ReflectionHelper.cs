using System.Collections;
using System.Reflection;

namespace Mic.Common;

/// <summary>Provides field access that treats properties and fields uniformly, by name, for data binding: lookup,
/// dotted-path reads, writes and conversion to a control's value type.</summary>
/// <remarks>A name resolves to a public instance property first, then a field, ignoring case.</remarks>
public static class ReflectionHelper {
  const BindingFlags Flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase;

  /// <summary>Finds a data field by name: a property first, then a field.</summary>
  /// <param name="type">The type to search.</param>
  /// <param name="name">The name of the property or field.</param>
  /// <returns>The member, or <see langword="null"/> when neither exists.</returns>
  public static MemberInfo? FindField(this Type type, string name) =>
    (MemberInfo?)type.GetProperty(name, Flags) ?? type.GetField(name, Flags);

  public static object? GetFieldValue(this MemberInfo field, object obj) {
    switch(field) {
      case PropertyInfo pi: return pi.GetValue(obj);
      case FieldInfo fi:    return fi.GetValue(obj);
    }
    return null;
  }

  /// <summary>Finds a field by name and reads its value.</summary>
  /// <param name="obj">The object to read from.</param>
  /// <param name="name">The name of the property or field.</param>
  /// <returns>The value, or <see langword="null"/> when the field does not exist or its value is
  /// <see langword="null"/>.</returns>
  public static object? GetFieldValue(this object obj, string name) {
    var field = obj.GetType().FindField(name);
    return field?.GetFieldValue(obj);
  }

  /// <summary>Reads the value at a dotted path, such as <c>"Address.City"</c>, against an object.</summary>
  /// <param name="obj">The object the path starts from.</param>
  /// <param name="dottedPath">The dotted field path.</param>
  /// <returns>The value, or <see langword="null"/> when the object or the path is empty, a step along the path is
  /// <see langword="null"/>, or a field is unknown.</returns>
  /// <remarks>A component-reference path, which starts with <c>@</c>, is resolved elsewhere; a path given here is
  /// a plain dotted field path.</remarks>
  public static object? GetTargetValue(object? obj, string? dottedPath) {
    if (obj == null || string.IsNullOrEmpty(dottedPath)) return null;
    object? current = obj;
    foreach (var segment in dottedPath.Split('.')) {
      if (current == null) return null;
      current = current.GetFieldValue(segment);
    }
    return current;
  }

  /// <summary>Reads the value at a dotted path and converts it to a control's value type.</summary>
  /// <typeparam name="T">The control's value type.</typeparam>
  /// <param name="obj">The object the path starts from.</param>
  /// <param name="dottedPath">The dotted field path.</param>
  /// <returns>The converted value, or the default of <typeparamref name="T"/> when the value is
  /// <see langword="null"/> or does not convert.</returns>
  public static T? GetTargetValue<T>(object? obj, string? dottedPath) => ConvertTo<T>(GetTargetValue(obj, dottedPath));

  /// <summary>Converts a resolved value to a control's value type.</summary>
  /// <typeparam name="T">The control's value type.</typeparam>
  /// <param name="value">The value to convert.</param>
  /// <returns>The converted value, or the default of <typeparamref name="T"/> when the value is
  /// <see langword="null"/> or does not convert.</returns>
  /// <remarks>A value already of the type is returned as is; a string target takes the value's text; anything else
  /// goes through <c>Convert.ChangeType</c>.</remarks>
  public static T? ConvertTo<T>(object? value) { TryConvertTo<T>(value, out var result); return result; }

  /// <summary>Converts a resolved value to a control's value type, reporting whether a non-null value converted.
  /// </summary>
  /// <typeparam name="T">The control's value type.</typeparam>
  /// <param name="value">The value to convert.</param>
  /// <param name="result">When this method returns, contains the converted value, or the default of
  /// <typeparamref name="T"/> when the value is <see langword="null"/> or did not convert.</param>
  /// <returns><see langword="true"/> if the value is <see langword="null"/> or converts; <see langword="false"/> if a
  /// non-null value cannot be converted to <typeparamref name="T"/>, such as a string bound to a numeric control.
  /// </returns>
  public static bool TryConvertTo<T>(object? value, out T? result) {
    if (value == null) { result = default; return true; }
    if (value is T typed) { result = typed; return true; }
    var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
    try {
      result = target == typeof(string) ? (T)(object)value.ToString()! : (T)System.Convert.ChangeType(value, target);
      return true;
    }
    catch { result = default; return false; }
  }

  public static void SetFieldValue(this MemberInfo field, object obj, object? value) {
    switch(field) {
      case PropertyInfo pi: pi.SetValue(obj, value); break;
      case FieldInfo fi:    fi.SetValue(obj, value); break;
    }
  }

  /// <summary>Finds a field by name and writes its value.</summary>
  /// <param name="obj">The object to write to.</param>
  /// <param name="name">The name of the property or field.</param>
  /// <param name="value">The value to write; no conversion is applied.</param>
  /// <param name="suppressError"><see langword="true"/> to swallow a failed write, such as a type mismatch;
  /// otherwise, <see langword="false"/>, and the failure throws.</param>
  /// <remarks>Nothing happens when the field does not exist.</remarks>
  public static void SetFieldValue(this object obj, string name, object? value, bool suppressError = false) {
    try {
      obj.GetType().FindField(name)?.SetFieldValue(obj, value);
    }
    catch when (suppressError) { }
  }

  /// <summary>Determines whether a type is one of the numeric value types.</summary>
  /// <param name="type">The type to test.</param>
  /// <returns><see langword="true"/> if the type is an integer, floating-point or decimal type; otherwise,
  /// <see langword="false"/>.</returns>
  public static bool IsNumeric(this Type type) =>
    type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte) ||
    type == typeof(sbyte) || type == typeof(uint) || type == typeof(ulong) || type == typeof(ushort) ||
    type == typeof(double) || type == typeof(float) || type == typeof(decimal);

  /// <summary>Determines whether a type is one of the integer value types, signed or unsigned.</summary>
  /// <param name="type">The type to test.</param>
  /// <returns><see langword="true"/> if the type is an integer type with no fractional part; otherwise,
  /// <see langword="false"/>.</returns>
  public static bool IsIntegerType(this Type type) =>
    type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte) ||
    type == typeof(uint) || type == typeof(ulong) || type == typeof(ushort) || type == typeof(sbyte);

  static readonly HashSet<Type> ScalarTypes = [
    typeof(string), typeof(decimal), typeof(Guid), typeof(DateTime), typeof(DateTimeOffset), typeof(DateOnly),
    typeof(TimeOnly), typeof(TimeSpan), typeof(byte[]), typeof(object), typeof(GeoPoint),
  ];

  /// <summary>Determines whether a value of a type is one a database column holds as itself: a primitive, an enum,
  /// or one of the framework types every provider maps.</summary>
  /// <param name="type">The type to test.</param>
  /// <returns><see langword="true"/> if a column holds the value as itself; <see langword="false"/> if a member of
  /// the type holds a document.</returns>
  /// <remarks>A nullable type counts as its underlying type.</remarks>
  public static bool IsScalarType(this Type type) {
    ArgumentNullException.ThrowIfNull(type);
    var underlying = Nullable.GetUnderlyingType(type) ?? type;
    return underlying.IsPrimitive || underlying.IsEnum || ScalarTypes.Contains(underlying);
  }

  public static Type? GetFieldReturnType(this MemberInfo field) {
    switch(field) {
      case PropertyInfo pi: return pi.PropertyType;
      case FieldInfo fi:    return fi.FieldType;
      case MethodInfo mi:   return mi.ReturnType;
    }
    return null;
  }

  /// <summary>Finds the single reference to <paramref name="oldObj"/> in the object graph under
  /// <paramref name="root"/> and replaces it with <paramref name="newObj"/>.</summary>
  /// <param name="root">The root of the object graph.</param>
  /// <param name="oldObj">The object to find.</param>
  /// <param name="newObj">The object to put in its place.</param>
  /// <returns><see langword="true"/> if the reference was found and replaced; otherwise, <see langword="false"/>.
  /// </returns>
  /// <remarks>A reference held in a field is reassigned. An item of a list is replaced by rebuilding the list with
  /// the new item and assigning the new list to its owner, so a grid that caches rows by list identity refreshes; a
  /// list field must therefore be resizable. The walk covers public instance properties and fields, guards against
  /// cycles, and skips strings, primitives and the known value types. The replaced object loses its identity, so a
  /// bound grid's row selection and expansion reset.</remarks>
  public static bool ReassignInGraph(object? root, object oldObj, object newObj) {
    if (root == null) return false;
    var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
    return Walk(root);

    bool Walk(object obj) {
      if (!visited.Add(obj)) return false;
      // A dictionary is the view's parameter map; an entry is reassigned the same way a field is.
      if (obj is IDictionary map) {
        var keys = new object[map.Count];
        map.Keys.CopyTo(keys, 0);                        // a snapshot: an entry may be reassigned mid-walk
        foreach (var key in keys) {
          var val = map[key];
          if (val == null) continue;
          if (ReferenceEquals(val, oldObj)) { map[key] = newObj; return true; }
          if (val is IList mlist) {
            for (int i = 0; i < mlist.Count; i++)
              if (ReferenceEquals(mlist[i], oldObj)) { map[key] = RebuildList(mlist, i, newObj); return true; }
            foreach (var item in mlist)
              if (item != null && !IsLeaf(item) && Walk(item)) return true;
          }
          else if (!IsLeaf(val) && Walk(val)) return true;
        }
        return false;
      }
      foreach (var field in DataFields(obj.GetType())) {
        var val = field.GetFieldValue(obj);
        if (val == null) continue;
        if (ReferenceEquals(val, oldObj)) { field.SetFieldValue(obj, newObj); return true; }
        if (val is IList list) {
          for (int i = 0; i < list.Count; i++)
            if (ReferenceEquals(list[i], oldObj)) { field.SetFieldValue(obj, RebuildList(list, i, newObj)); return true; }
          foreach (var item in list)
            if (item != null && !IsLeaf(item) && Walk(item)) return true;
        }
        else if (!IsLeaf(val) && Walk(val)) return true;
      }
      return false;
    }
  }

  private static IEnumerable<MemberInfo> DataFields(Type type) {
    foreach (var p in type.GetProperties(Flags)) if (p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0) yield return p;
    foreach (var f in type.GetFields(Flags)) yield return f;
  }

  private static bool IsLeaf(object v) {
    var type = v.GetType();
    return type.IsPrimitive || type.IsEnum || v is string or decimal or DateTime or DateOnly or TimeOnly or TimeSpan or Guid;
  }

  private static IList RebuildList(IList src, int replaceIndex, object newItem) {
    var copy = (IList)Activator.CreateInstance(src.GetType())!;
    for (int i = 0; i < src.Count; i++) copy.Add(i == replaceIndex ? newItem : src[i]);
    return copy;
  }
}
