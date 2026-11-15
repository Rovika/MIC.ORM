using System.Reflection;
using System.Reflection.Emit;

namespace Mic.Emit;

/// <summary>Builds a class that inherits a base type and implements interfaces, with every member forwarding to the
/// method an <see cref="EmitController"/> nominates for it.</summary>
/// <remarks>The emitter decides how a class is built and never what its members do; everything specific to a
/// consumer arrives through the controller.</remarks>
public class ClassEmitter {
  readonly ModuleBuilder _module;
  int _emitted;

  /// <summary>Initializes a new instance of the <see cref="ClassEmitter"/> class, with one dynamic assembly named
  /// after the caller.</summary>
  /// <param name="assemblyName">The name of the dynamic assembly the emitted classes go in, as a stack trace shows
  /// it. The default is <c>"Mic.Emit.Dynamic"</c>.</param>
  /// <remarks>Give each emitter a distinct name: two dynamic assemblies of the same name coexist with distinct types,
  /// but the types share their assembly-qualified name, so anything that keys on it gets a wrong answer. The assembly
  /// is not collectible; an emitted class lives as long as the process.</remarks>
  public ClassEmitter(string assemblyName = "Mic.Emit.Dynamic") {
    var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(assemblyName), AssemblyBuilderAccess.Run);
    _module = assembly.DefineDynamicModule(assemblyName);
  }

  /// <summary>Builds a class over <paramref name="baseType"/> that implements <paramref name="interfaces"/>.</summary>
  /// <param name="controller">The controller that says what each member does.</param>
  /// <param name="baseType">The base class; it must not be sealed and must have a public or protected constructor.
  /// </param>
  /// <param name="interfaces">The interfaces to implement, at least one; the interfaces they extend are implemented
  /// too.</param>
  /// <returns>The emitted type.</returns>
  /// <remarks>The class gets one forwarding constructor per public or protected base constructor. A member the
  /// controller returns no spec for throws <c>NotSupportedException</c> when called.</remarks>
  public Type EmitClass(EmitController controller, Type baseType, params Type[] interfaces) {
    ArgumentNullException.ThrowIfNull(controller);
    ArgumentNullException.ThrowIfNull(baseType);
    if (interfaces.Length == 0)
      throw new ArgumentException("A class must implement at least one interface.", nameof(interfaces));
    foreach (var contract in interfaces)
      if (!contract.IsInterface)
        throw new ArgumentException($"'{contract.Name}' is not an interface.", nameof(interfaces));
    if (baseType.IsSealed)
      throw new ArgumentException($"'{baseType.Name}' is sealed and cannot be inherited.", nameof(baseType));

    // A counter: a model may build two classes over the same interface, and a name collision fails at DefineType.
    var name = $"{controller.GetClassName(interfaces)}_{++_emitted}";
    var type = _module.DefineType(name,
      TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.BeforeFieldInit, baseType, interfaces);

    foreach (var attribute in controller.GetClassAttributes(interfaces))
      type.SetCustomAttribute(AttributeCopier.ToBuilder(attribute));

    EmitConstructors(type, baseType);

    // Every interface and every interface those extend; reflection does not roll them up. The member counter runs
    // across the whole class, not per interface (see EmitMember.Index).
    var index = 0;
    foreach (var contract in interfaces.SelectMany(Closure).Distinct())
      EmitInterface(type, contract, controller, ref index);

    return type.CreateType();
  }

  static IEnumerable<Type> Closure(Type contract) => [contract, .. contract.GetInterfaces()];

  // ----- constructors ------------------------------------------------------------------------------------------------

  /// <summary>Emits one forwarding constructor per public or protected base constructor.</summary>
  static void EmitConstructors(TypeBuilder type, Type baseType) {
    var baseCtors = baseType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
      .Where(c => c.IsPublic || c.IsFamily || c.IsFamilyOrAssembly)
      .ToArray();
    if (baseCtors.Length == 0)
      throw new ArgumentException(
        $"'{baseType.Name}' has no constructor an inheritor can call.", nameof(baseType));

    foreach (var baseCtor in baseCtors) {
      var parameters = baseCtor.GetParameters();
      var ctor = type.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard,
        [.. parameters.Select(p => p.ParameterType)]);
      var il = ctor.GetILGenerator();
      il.Emit(OpCodes.Ldarg_0);
      for (var i = 0; i < parameters.Length; i++)
        il.Emit(OpCodes.Ldarg, i + 1);
      il.Emit(OpCodes.Call, baseCtor);
      il.Emit(OpCodes.Ret);
    }
  }

  // ----- members -----------------------------------------------------------------------------------------------------

  static void EmitInterface(TypeBuilder type, Type contract, EmitController controller, ref int index) {
    foreach (var property in contract.GetProperties()) {
      var member = property;
      var emitted = type.DefineProperty(property.Name, PropertyAttributes.None, property.PropertyType,
        [.. property.GetIndexParameters().Select(p => p.ParameterType)]);
      var position = index++;

      if (property.GetGetMethod() is { } getter)
        emitted.SetGetMethod(EmitMethod(type, controller,
          new EmitMember(getter, member, EmitMemberKind.PropertyGet, position)));
      if (property.GetSetMethod() is { } setter)
        emitted.SetSetMethod(EmitMethod(type, controller,
          new EmitMember(setter, member, EmitMemberKind.PropertySet, position)));

      foreach (var attribute in controller.GetMemberAttributes(
          new EmitMember(property.GetGetMethod() ?? property.GetSetMethod()!, member, EmitMemberKind.PropertyGet,
            position)))
        emitted.SetCustomAttribute(AttributeCopier.ToBuilder(attribute));
    }

    // Accessors are methods too; they were emitted with their properties above.
    foreach (var method in contract.GetMethods().Where(m => !m.IsSpecialName))
      EmitMethod(type, controller, new EmitMember(method, null, EmitMemberKind.Method, index++));
  }

  static MethodBuilder EmitMethod(TypeBuilder type, EmitController controller, EmitMember member) {
    var declaration = member.Declaration;
    var parameters = declaration.GetParameters();

    // Final, NewSlot and Virtual is an interface implementation in IL; SpecialName marks an accessor, so GetProperties
    // on the emitted type finds a property.
    var attributes = MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig
      | MethodAttributes.NewSlot | MethodAttributes.Final;
    if (declaration.IsSpecialName)
      attributes |= MethodAttributes.SpecialName;

    var method = type.DefineMethod(declaration.Name, attributes, declaration.ReturnType,
      [.. parameters.Select(p => p.ParameterType)]);
    for (var i = 0; i < parameters.Length; i++)
      method.DefineParameter(i + 1, ParameterAttributes.None, parameters[i].Name);

    var il = method.GetILGenerator();
    var spec = controller.GetMemberSpec(member);
    if (spec == null)
      EmitRefusal(il, member);
    else
      EmitCall(il, spec, member);

    type.DefineMethodOverride(method, declaration);

    if (member.Kind == EmitMemberKind.Method)
      foreach (var attribute in controller.GetMemberAttributes(member))
        method.SetCustomAttribute(AttributeCopier.ToBuilder(attribute));

    return method;
  }

  static readonly ConstructorInfo NotSupported =
    typeof(NotSupportedException).GetConstructor([typeof(string)])!;

  /// <summary>Emits a body that throws, naming the member, for a member the controller had no behaviour for.</summary>
  static void EmitRefusal(ILGenerator il, EmitMember member) {
    il.Emit(OpCodes.Ldstr, $"'{member.Interface.Name}.{member.Name}' has no implementation in the emitted class.");
    il.Emit(OpCodes.Newobj, NotSupported);
    il.Emit(OpCodes.Throw);
  }

  static void EmitCall(ILGenerator il, EmitSpec spec, EmitMember member) {
    // The call target: this, or a field or property of the base class.
    il.Emit(OpCodes.Ldarg_0);
    switch (spec.Target) {
      case FieldInfo field:
        il.Emit(OpCodes.Ldfld, field);
        break;
      case PropertyInfo property:
        var getter = property.GetGetMethod(nonPublic: true)
          ?? throw new ArgumentException($"Target property '{property.Name}' has no getter.");
        il.Emit(getter.IsVirtual ? OpCodes.Callvirt : OpCodes.Call, getter);
        break;
    }

    var targetParameters = spec.Method.GetParameters();
    for (var i = 0; i < spec.Arguments.Count; i++)
      EmitArgument(il, spec.Arguments[i], targetParameters[i].ParameterType, member);

    il.Emit(spec.Method.IsVirtual ? OpCodes.Callvirt : OpCodes.Call, spec.Method);

    var wanted = member.Declaration.ReturnType;
    if (wanted == typeof(void)) {
      // A void member forwarding to a method that returns a value must pop it, or the stack is unbalanced.
      if (spec.Method.ReturnType != typeof(void))
        il.Emit(OpCodes.Pop);
    } else {
      if (spec.Method.ReturnType == typeof(void))
        throw new ArgumentException(
          $"{member} returns {wanted.Name}, but '{spec.Method.Name}' returns nothing.");
      EmitConversion(il, spec.Method.ReturnType, wanted);
    }
    il.Emit(OpCodes.Ret);
  }

  static void EmitArgument(ILGenerator il, EmitArg argument, Type wanted, EmitMember member) {
    switch (argument) {
      case EmitArg.ParameterArg parameter:
        var parameters = member.Declaration.GetParameters();
        if (parameter.Index >= parameters.Length)
          throw new ArgumentException(
            $"{member} takes {parameters.Length} parameter(s); the spec asked for number {parameter.Index}.");
        il.Emit(OpCodes.Ldarg, parameter.Index + 1);
        EmitConversion(il, parameters[parameter.Index].ParameterType, wanted);
        break;

      case EmitArg.ConstantArg constant:
        EmitConstant(il, constant.Value, wanted);
        break;

      default:
        throw new ArgumentException($"Unknown argument kind {argument.GetType().Name}.");
    }
  }

  /// <summary>Emits a box, unbox or cast as the pair of types calls for, and nothing when they agree.</summary>
  static void EmitConversion(ILGenerator il, Type from, Type to) {
    if (from == to)
      return;
    if (to.IsAssignableFrom(from)) {
      // A value type widens to a reference type by boxing; a reference widening needs no instruction.
      if (from.IsValueType)
        il.Emit(OpCodes.Box, from);
      return;
    }
    // Narrowing: unbox.any unboxes a value type and casts a reference one.
    il.Emit(OpCodes.Unbox_Any, to);
  }

  static void EmitConstant(ILGenerator il, object? value, Type wanted) {
    if (value == null) {
      if (wanted.IsValueType && Nullable.GetUnderlyingType(wanted) == null)
        throw new ArgumentException($"null cannot be passed where {wanted.Name} is wanted.");
      il.Emit(OpCodes.Ldnull);
      return;
    }

    var type = value.GetType();
    if (type.IsEnum) {
      value = Convert.ChangeType(value, Enum.GetUnderlyingType(type));
      type = value.GetType();
    }

    switch (value) {
      case string text:
        il.Emit(OpCodes.Ldstr, text);
        break;
      case bool flag:
        il.Emit(flag ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
        type = typeof(bool);
        break;
      case Type carried:
        il.Emit(OpCodes.Ldtoken, carried);
        il.Emit(OpCodes.Call, TypeFromHandle);
        type = typeof(Type);
        break;
      case long number:
        il.Emit(OpCodes.Ldc_I8, number);
        break;
      case double number:
        il.Emit(OpCodes.Ldc_R8, number);
        break;
      case float number:
        il.Emit(OpCodes.Ldc_R4, number);
        break;
      case byte or sbyte or short or ushort or int or uint or char:
        il.Emit(OpCodes.Ldc_I4, Convert.ToInt32(value));
        break;
      default:
        throw new ArgumentException(
          $"A constant of type {type.Name} cannot be written into IL. Use a string, a number, a bool, an enum or a "
          + "Type — anything else has to reach the emitted class through its base.");
    }

    EmitConversion(il, type, wanted);
  }

  static readonly MethodInfo TypeFromHandle =
    typeof(Type).GetMethod(nameof(Type.GetTypeFromHandle), [typeof(RuntimeTypeHandle)])!;
}
