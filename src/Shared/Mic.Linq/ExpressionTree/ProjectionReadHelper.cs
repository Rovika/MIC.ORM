using System.Reflection;
using Mic.Linq.Model;
namespace Mic.Linq.ExpressionTree;

/// <summary>Represents the method that reads one named value for a row factory, as the member receiving it wants
/// it.</summary>
/// <param name="name">The name of the column or slot.</param>
/// <param name="wanted">The type the receiving member wants.</param>
/// <param name="value">When this method returns, contains the value, or <see langword="null"/> if the row has no
/// such name.</param>
/// <returns><see langword="true"/> if the row has a value of that name; otherwise, <see langword="false"/>.</returns>
public delegate bool RowValueReader(string name, Type wanted, out object? value);

/// <summary>Turns projected rows, which come back as named values, into instances of the type the caller projected
/// into.</summary>
/// <remarks>Values are matched to constructor parameters and properties by name, never by position. The factory for
/// a type is made once, on first use, and reads through a <see cref="RowValueReader"/>, so a row of a hand-written
/// statement is read by column name through the same factory.</remarks>
public static class ProjectionReadHelper {
  static readonly MethodInfo BuildGroupsMethod =
    typeof(ProjectionReadHelper).GetMethod(nameof(BuildGroups), BindingFlags.NonPublic | BindingFlags.Static)!;

  /// <summary>Turns projected rows into instances of <typeparamref name="T"/>.</summary>
  /// <typeparam name="T">The type the rows were projected into.</typeparam>
  /// <param name="rows">The projected rows, each an <see cref="ILinqRow"/>.</param>
  /// <returns>A list that contains one instance per row.</returns>
  public static List<T> Materialize<T>(System.Collections.IEnumerable rows) {
    var create = GetFactory<T>();
    var list = new List<T>();
    foreach (var row in rows) {
      var linqRow = (ILinqRow)row!;
      list.Add(create((string name, Type wanted, out object? value) => {
        value = Read(linqRow, name, wanted);
        return true;
      }));
    }
    return list;
  }

  /// <summary>Turns the values of a projection into an object of one value into instances of
  /// <typeparamref name="T"/>.</summary>
  /// <typeparam name="T">The type the values were projected into.</typeparam>
  /// <param name="values">The values, one per row.</param>
  /// <param name="name">The name <typeparamref name="T"/> takes the value under: a constructor parameter or a
  /// property.</param>
  /// <returns>A list that contains one instance per value.</returns>
  public static List<T> MaterializeValues<T>(System.Collections.IEnumerable values, string name) {
    var create = GetFactory<T>();
    var list = new List<T>();
    foreach (var item in values)
      list.Add(create((string wantedName, Type wanted, out object? value) => {
        var isTheValue = string.Equals(wantedName, name, StringComparison.OrdinalIgnoreCase);
        value = isTheValue ? ConvertValue(item, wanted) : null;
        return isTheValue;
      }));
    return list;
  }

  /// <summary>Turns the groups of a grouping returned as groups into the typed groups the caller declared.</summary>
  /// <typeparam name="T">The type of a group, an <see cref="IGrouping{TKey, TElement}"/>.</typeparam>
  /// <param name="groups">The groups, each an <see cref="IGrouping{TKey, TElement}"/> of objects.</param>
  /// <param name="keyValueName">The name the key's type takes its one value under, for a key written as an object
  /// of one value; otherwise, <see langword="null"/>.</param>
  /// <returns>A list that contains one <see cref="RowGrouping{TKey, TElement}"/> per group.</returns>
  /// <remarks>A key of several values comes as a row of named values and is built into the type the caller wrote;
  /// a row of a group is an entity or a value and is taken as it is.</remarks>
  public static List<T> MaterializeGroups<T>(System.Collections.IEnumerable groups, string? keyValueName) {
    var build = BuildGroupsMethod.MakeGenericMethod(typeof(T).GetGenericArguments());
    return (List<T>)build.Invoke(null, [groups, keyValueName])!;
  }

  private static List<IGrouping<TKey, TElement>> BuildGroups<TKey, TElement>(System.Collections.IEnumerable groups,
      string? keyValueName) {
    var list = new List<IGrouping<TKey, TElement>>();
    foreach (var item in groups) {
      var group = (IGrouping<object?, object?>)item!;
      var key = keyValueName == null
        ? ConvertGroupPart<TKey>(group.Key)
        : MaterializeValues<TKey>(new[] { group.Key }, keyValueName)[0];
      list.Add(new RowGrouping<TKey, TElement>(key, [.. group.Select(ConvertGroupPart<TElement>)]));
    }
    return list;
  }

  /// <summary>Converts a key or a row of a group to the type the caller declared: taken as it is, built from a row
  /// of named values, or converted as a value.</summary>
  private static TPart ConvertGroupPart<TPart>(object? part) => part switch {
    null => default!,
    TPart typed => typed,
    ILinqRow row => GetFactory<TPart>()((string name, Type wanted, out object? value) => {
      value = Read(row, name, wanted);
      return true;
    }),
    _ => (TPart)ConvertValue(part, typeof(TPart))!,
  };

  /// <summary>Gets the factory that builds a <typeparamref name="T"/> out of named values.</summary>
  /// <typeparam name="T">The type to build.</typeparam>
  /// <returns>A function that builds an instance from a reader of named values.</returns>
  /// <exception cref="NotSupportedException">The type has neither a constructor whose parameters are named after the
  /// selected values nor a parameterless constructor with settable properties.</exception>
  public static Func<RowValueReader, T> GetFactory<T>() =>
    RowFactories<T>.Create
      ?? throw new NotSupportedException(
        $"'{typeof(T).Name}' cannot be built from a row: it needs either one constructor whose parameters are "
        + "named after the selected values, or a parameterless constructor and settable properties.");

  /// <summary>Reads one selected value of a row, as the member that receives it wants it.</summary>
  /// <param name="row">The projected row.</param>
  /// <param name="name">The name of the value.</param>
  /// <param name="wanted">The type the receiving member wants.</param>
  /// <returns>The value, converted to the wanted type.</returns>
  /// <remarks>The row's values already have the type the projection's model member declares; this converts a
  /// widening the caller asked for by declaring a different type on the target, and an enum out of its underlying
  /// value.</remarks>
  public static object? Read(ILinqRow row, string name, Type wanted) => ConvertValue(row.GetValue(name), wanted);

  /// <summary>Converts a value to the type the member receiving it wants.</summary>
  /// <param name="value">The value.</param>
  /// <param name="wanted">The type the receiving member wants.</param>
  /// <returns>The value itself if it already has the type; an enum made out of its underlying value; otherwise, a
  /// converted value.</returns>
  public static object? ConvertValue(object? value, Type wanted) {
    if (value == null || wanted.IsInstanceOfType(value))
      return value;
    var target = Nullable.GetUnderlyingType(wanted) ?? wanted;
    return target.IsEnum ? Enum.ToObject(target, value) : Convert.ChangeType(value, target);
  }
}
