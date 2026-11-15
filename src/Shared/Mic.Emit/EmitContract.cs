using System.Reflection;

namespace Mic.Emit;

/// <summary>Represents the member being emitted, as the controller is asked about it.</summary>
/// <param name="Declaration">The interface method being implemented; a property accessor is a method here, as it is
/// in IL.</param>
/// <param name="Property">The property the accessor belongs to, or <see langword="null"/> for an ordinary method.
/// </param>
/// <param name="Kind">One of the enumeration values that says whether the member is a getter, a setter or a method.
/// </param>
/// <param name="Index">The zero-based position of the member among all the emitted members of the class, counting
/// properties and methods rather than accessors, so a property's getter and setter share one index.</param>
/// <remarks>The index runs across the whole class, not per interface, since an interface reports only its own
/// members; a consumer may store values by it. What the number means is the caller's business, and a caller that
/// needs a particular meaning should map by name, since reflection makes no promise about the order it reports
/// members in.</remarks>
public readonly record struct EmitMember(
  MethodInfo Declaration, PropertyInfo? Property, EmitMemberKind Kind, int Index) {
  /// <summary>Gets the interface the member is declared on.</summary>
  public Type Interface => Declaration.DeclaringType!;

  /// <summary>Gets the property's type for an accessor, or the return type for a method.</summary>
  public Type ValueType => Property?.PropertyType ?? Declaration.ReturnType;

  public string Name => Property?.Name ?? Declaration.Name;

  public override string ToString() => $"{Interface.Name}.{Name} ({Kind})";
}

public enum EmitMemberKind { PropertyGet, PropertySet, Method }

/// <summary>Represents what a member does: call <see cref="Method"/> on <see cref="Target"/> with
/// <see cref="Arguments"/>.</summary>
/// <remarks>A member does exactly one thing: it calls one instance method on one target with a fixed argument list.
/// A <see langword="null"/> <see cref="Target"/> means the emitted instance itself; otherwise it is a field or
/// property of the base class. A generic method must be closed with <c>MakeGenericMethod</c> before it is handed
/// over.</remarks>
public sealed class EmitSpec {
  public EmitSpec(MemberInfo? target, MethodInfo method, params EmitArg[] arguments) {
    ArgumentNullException.ThrowIfNull(method);
    if (target is not (null or FieldInfo or PropertyInfo))
      throw new ArgumentException($"A target must be a field or a property; got {target.GetType().Name}.",
        nameof(target));
    if (method.IsStatic)
      throw new ArgumentException($"'{method.Name}' is static; a spec calls an instance method.", nameof(method));
    Target = target;
    Method = method;
    Arguments = arguments;

    var expected = method.GetParameters().Length;
    if (arguments.Length != expected)
      throw new ArgumentException(
        $"'{method.Name}' takes {expected} argument(s) and {arguments.Length} were supplied.", nameof(arguments));
  }

  /// <summary>Gets the field or property of the base class the call is made on.</summary>
  /// <value>The member, or <see langword="null"/> to call on the emitted instance itself.</value>
  public MemberInfo? Target { get; }

  public MethodInfo Method { get; }
  public IReadOnlyList<EmitArg> Arguments { get; }
}

/// <summary>Defines where one argument of a member's call comes from, and provides a base for the two sources: a
/// value fixed when the class is built, and a parameter the caller passed in.</summary>
/// <remarks>For a property setter the incoming value is parameter 0, as it is in IL.</remarks>
public abstract class EmitArg {
  /// <summary>Creates an argument whose value is baked into the emitted code.</summary>
  /// <param name="value">The value: <see langword="null"/>, a string, a Boolean, an integer, a floating-point number,
  /// an enum or a <c>Type</c>.</param>
  /// <returns>The argument.</returns>
  public static EmitArg Constant(object? value) => new ConstantArg(value);

  /// <summary>Creates an argument that is one of the emitted member's own parameters.</summary>
  /// <param name="index">The zero-based index of the parameter; for a property setter, 0 is the value.</param>
  /// <returns>The argument.</returns>
  public static EmitArg Parameter(int index) => index >= 0
    ? new ParameterArg(index)
    : throw new ArgumentOutOfRangeException(nameof(index));

  internal sealed class ConstantArg(object? value) : EmitArg {
    public object? Value { get; } = value;
    public override string ToString() => Value?.ToString() ?? "null";
  }

  internal sealed class ParameterArg(int index) : EmitArg {
    public int Index { get; } = index;
    public override string ToString() => $"arg{Index}";
  }
}

/// <summary>Defines the core behavior of the caller's policy for an emitted class, what each member does and what is
/// copied onto the class, and provides a base for derived classes.</summary>
/// <remarks>The emitter decides how a class is built; the controller decides what its members do, one method on one
/// target per member. A derived class must override <see cref="GetMemberSpec"/>.</remarks>
public abstract class EmitController {
  /// <summary>When overridden in a derived class, gets what the specified member does.</summary>
  /// <param name="member">The member being emitted.</param>
  /// <returns>The spec, or <see langword="null"/> to leave the member unimplemented, in which case the emitted class
  /// throws when the member is called.</returns>
  /// <remarks>Unimplemented is a valid answer: an interface may carry members a consumer has no behaviour for, and
  /// the rest of the class stays usable.</remarks>
  public abstract EmitSpec? GetMemberSpec(EmitMember member);

  /// <summary>When overridden in a derived class, gets the attributes to copy onto the emitted member.</summary>
  /// <param name="member">The member being emitted.</param>
  /// <returns>The attributes, as reflection reports them. The default is none.</returns>
  public virtual IEnumerable<CustomAttributeData> GetMemberAttributes(EmitMember member) => [];

  public virtual IEnumerable<CustomAttributeData> GetClassAttributes(Type[] interfaces) => [];

  /// <summary>When overridden in a derived class, gets the name of the emitted class, as a stack trace or a debugger
  /// shows it.</summary>
  /// <param name="interfaces">The interfaces the class implements.</param>
  /// <returns>The name. The default is the first interface's name without its leading <c>I</c>, followed by
  /// <c>_Emitted</c>.</returns>
  public virtual string GetClassName(Type[] interfaces) {
    var first = interfaces.Length > 0 ? interfaces[0].Name : "Object";
    if (first.Length > 1 && first[0] == 'I' && char.IsUpper(first[1]))
      first = first[1..];
    return first + "_Emitted";
  }
}
