using Mic.Linq.Model;
using Mic.Linq.Nodes;
using Mic.Modeling;

namespace Mic.Orm.Model;

/// <summary>Represents a transient entity the query engine asked for, for a query: a projection's rows, a grouping's
/// keys, a bundle's slots or the rows of a JSON array.</summary>
/// <remarks>Named, typed, nullable value members in a fixed order, with no key, table or record. Made in one call by
/// <see cref="ILinqDataModel.CreateTransientEntity"/> and never changed after. A row of it is carried as an
/// <c>IEntityData</c> (<see cref="Runtime.ProjectionData"/>) over the <see cref="EntityInfo"/>.</remarks>
public sealed class TransientEntityInfo : ILinqEntity {
  public string Name => EntityInfo.Name;

  /// <summary>Gets the structure a row of this entity is carried in.</summary>
  public EntityInfo EntityInfo { get; }

  public IReadOnlyList<TransientMemberInfo> Members { get; }

  public TransientEntityInfo(string name, IReadOnlyList<LinqMemberSpec> members) {
    ArgumentException.ThrowIfNullOrEmpty(name);
    ArgumentNullException.ThrowIfNull(members);
    EntityInfo = new EntityInfo(name, EntityKind.ModelStructure);
    var made = new TransientMemberInfo[members.Count];
    for (var i = 0; i < made.Length; i++) {
      var spec = members[i];
      var field = new Field(spec.Name, new ScalarTypeRef(spec.DataType), spec.IsNullable ? FieldFlags.Nullable : FieldFlags.None);
      EntityInfo.AddField(field);
      made[i] = new TransientMemberInfo(this, field, spec.DataType);
    }
    Members = made;
  }

  /// <summary>Finds a member by name, ignoring case.</summary>
  /// <param name="name">The member name.</param>
  /// <returns>The member, or <see langword="null"/> if none has that name.</returns>
  public TransientMemberInfo? FindMember(string name) {
    foreach (var member in Members)
      if (string.Equals(member.Name, name, StringComparison.OrdinalIgnoreCase))
        return member;
    return null;
  }

  public override string ToString() => Name;

  IReadOnlyList<ILinqEntityMember> ILinqEntity.Members => Members;

  ILinqEntityMember? ILinqEntity.FindMember(string name) => FindMember(name);

  IReadOnlyList<ILinqEntityMember> ILinqEntity.PrimaryKeyMembers => [];

  bool ILinqEntity.IsTransient => true;
  bool ILinqEntity.IsCached => false;
  bool ILinqEntity.IsReadOnly => true;
}

/// <summary>Represents a value member of a <see cref="TransientEntityInfo"/>, over the field a row stores it under.
///</summary>
public sealed class TransientMemberInfo : ILinqEntityMember {
  public TransientEntityInfo Entity { get; }

  /// <summary>Gets the field of the entity's structure that a row is read and written by.</summary>
  public Field Field { get; }

  public Type DataType { get; }

  public string Name => Field.Name;

  public bool IsNullable => Field.Flags.HasFlag(FieldFlags.Nullable);

  internal TransientMemberInfo(TransientEntityInfo entity, Field field, Type type) {
    Entity = entity;
    Field = field;
    DataType = type;
  }

  public override string ToString() => $"{Entity.Name}.{Name}";

  Type? ILinqEntityMember.DataType => DataType;

  LinqMemberKind ILinqEntityMember.Kind => LinqMemberKind.Value;

  ILinqEntity? ILinqEntityMember.Entity => Entity;

  ILinqReferenceInfo? ILinqEntityMember.ReferenceInfo => null;

  ILinqMemberListInfo? ILinqEntityMember.ListInfo => null;
}
