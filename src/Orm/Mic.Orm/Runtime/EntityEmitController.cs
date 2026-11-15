using System.Reflection;
using Mic.Emit;
using Mic.Orm.Model;
namespace Mic.Orm.Runtime;

/// <summary>Tells the emitter what each property of an entity class does: a getter reads the record at the member's
/// position and a setter writes it.</summary>
public class EntityEmitController(EntityTypeInfo entity) : EmitController {
  static readonly PropertyInfo RecordProperty = typeof(EntityBase).GetProperty(nameof(EntityBase.Record))!;

  static readonly MethodInfo GetValueOpen = typeof(EntityRecord).GetMethods()
    .Single(m => m.Name == nameof(EntityRecord.GetValue) && m.IsGenericMethodDefinition);

  static readonly MethodInfo SetValueOpen = typeof(EntityRecord).GetMethods()
    .Single(m => m.Name == nameof(EntityRecord.SetValue) && m.IsGenericMethodDefinition);

  public EntityTypeInfo Entity { get; } = entity;

  /// <summary>Gets the specification of one member: a read or a write of the record at the member's position, for
  /// every kind of member.</summary>
  /// <param name="member">The interface member to implement.</param>
  /// <returns>The specification, or <see langword="null"/> for a member the model does not know or for a
  /// method.</returns>
  /// <remarks>The constant baked in is the member's position in <see cref="EntityTypeInfo.Members"/>, which the record
  /// resolves to a storage slot on every call. A reference is read and written like a column; that its slot holds a
  /// stub, and that reading through it may load, is behind <c>EntityRecord.GetValue</c>.</remarks>
  public override EmitSpec? GetMemberSpec(EmitMember member) {
    // A property the model does not know is left unimplemented; the model builder has already reported it.
    if (Entity.FindMember(member.Name) is not { } target)
      return null;
    var index = target.Index;

    return member.Kind switch {
      EmitMemberKind.PropertyGet => new EmitSpec(RecordProperty,
        GetValueOpen.MakeGenericMethod(member.ValueType), EmitArg.Constant(index)),

      EmitMemberKind.PropertySet => new EmitSpec(RecordProperty,
        SetValueOpen.MakeGenericMethod(member.ValueType),
        EmitArg.Constant(index), EmitArg.Parameter(0)),

      // An entity interface may declare methods; nothing implements one yet.
      _ => null,
    };
  }

  /// <summary>Gets the name of the emitted class: the entity's name with an <c>_Emitted</c> suffix.</summary>
  /// <param name="interfaces">The interfaces the class implements.</param>
  /// <returns>The class name.</returns>
  public override string GetClassName(Type[] interfaces) => Entity.Name + "_Emitted";
}
