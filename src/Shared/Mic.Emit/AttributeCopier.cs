using System.Reflection;
using System.Reflection.Emit;

namespace Mic.Emit;

/// <summary>Converts an attribute as reflection reports it into one the emitter can write. This class cannot be
/// inherited.</summary>
/// <remarks>A caller names the attributes it wants as <c>CustomAttributeData</c>, which
/// <c>member.GetCustomAttributesData()</c> gives, and never touches <c>System.Reflection.Emit</c>.</remarks>
static class AttributeCopier {
  public static CustomAttributeBuilder ToBuilder(CustomAttributeData data) {
    var constructorArguments = data.ConstructorArguments.Select(ReadValue).ToArray();

    var properties = new List<PropertyInfo>();
    var propertyValues = new List<object?>();
    var fields = new List<FieldInfo>();
    var fieldValues = new List<object?>();

    foreach (var named in data.NamedArguments) {
      if (named.IsField) {
        fields.Add((FieldInfo)named.MemberInfo);
        fieldValues.Add(ReadValue(named.TypedValue));
      } else {
        properties.Add((PropertyInfo)named.MemberInfo);
        propertyValues.Add(ReadValue(named.TypedValue));
      }
    }

    return new CustomAttributeBuilder(data.Constructor, constructorArguments,
      properties.ToArray(), propertyValues.ToArray(), fields.ToArray(), fieldValues.ToArray());
  }

  /// <summary>Reads an argument's value, rebuilding an array argument, which arrives as a read-only collection of
  /// typed values, as an array of the right element type.</summary>
  static object? ReadValue(CustomAttributeTypedArgument argument) {
    if (argument.Value is not IReadOnlyList<CustomAttributeTypedArgument> items)
      return argument.Value;

    var elementType = argument.ArgumentType.GetElementType()!;
    var array = Array.CreateInstance(elementType, items.Count);
    for (var i = 0; i < items.Count; i++)
      array.SetValue(ReadValue(items[i]), i);
    return array;
  }
}
