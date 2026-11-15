using Mic.Linq.Model;
using Mic.Modeling;
using Mic.Orm.Model;

namespace Mic.Orm.Runtime;

/// <summary>Represents one row of a transient entity, such as a projection's row or a composite group key: named
/// values over the structure the entity was made with.</summary>
/// <remarks>As an <see cref="IEntityData"/> it binds wherever a query result's row does, on the client and through
/// JSON; as an <see cref="ILinqRow"/> the query engine reads and writes it. It has no identity, no change tracking
/// and no entity instance.</remarks>
public sealed class ProjectionData : IEntityData, ILinqRow {
  public TransientEntityInfo Entity { get; }

  public EntityInfo EntityInfo => Entity.EntityInfo;

  readonly object?[] _values;

  public ProjectionData(TransientEntityInfo entity) {
    ArgumentNullException.ThrowIfNull(entity);
    Entity = entity;
    _values = new object?[entity.Members.Count];
  }

  public object? GetValue(Field field) => _values[field.DataIndex];

  public void SetValue(Field field, object? value) => _values[field.DataIndex] = value;

  public object? GetValue(string fieldName) => GetValue(EntityInfo.GetField(fieldName));

  public void SetValue(string fieldName, object? value) => SetValue(EntityInfo.GetField(fieldName), value);

  public object? GetValue(ILinqEntityMember member) => GetValue(((TransientMemberInfo)member).Field);

  public void SetValue(ILinqEntityMember member, object? value) => SetValue(((TransientMemberInfo)member).Field, value);

  public override string ToString() =>
    string.Join(", ", EntityInfo.Fields.Select(f => $"{f.Name}={GetValue(f) ?? "null"}"));

  ILinqEntity ILinqRow.Entity => Entity;
}
