using Mic.Orm.Model;
namespace Mic.Orm.Runtime;

/// <summary>Represents what a queryable property's result is cached under for one owner: the member, the query's
/// shape and the values its parameters took.</summary>
internal sealed class CachedResultKey(EntityMemberInfo member, ulong shape, object?[] values) : IEquatable<CachedResultKey> {
  readonly EntityMemberInfo _member = member;
  readonly ulong _shape = shape;
  readonly object?[] _values = values;

  public bool Equals(CachedResultKey? other) {
    if (other == null || !ReferenceEquals(_member, other._member) || _shape != other._shape
        || _values.Length != other._values.Length)
      return false;
    for (var i = 0; i < _values.Length; i++)
      if (!Equals(_values[i], other._values[i]))
        return false;
    return true;
  }

  public override bool Equals(object? obj) => Equals(obj as CachedResultKey);

  public override int GetHashCode() => HashCode.Combine(_member, _shape);
}
