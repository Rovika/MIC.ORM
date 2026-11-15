using Mic.Linq.Model;
using Mic.Linq.Nodes;

namespace Mic.Linq.Translation;

/// <summary>Represents what a grouping produces rows of: a structure whose members are the keys, by name, which also
/// remembers the source entity an aggregate over the group names members of and the key expressions themselves.
///</summary>
/// <remarks>The shape decorates the entity a row of the keys is a row of, so that a member named off a group row
/// binds the way a member of any row does. It is built once, at binding, and read by the transform.</remarks>
public sealed class GroupShape : ILinqEntity {
  private GroupShape(ILinqEntity entity, ILinqEntity sourceEntity, IReadOnlyList<QueryNode> keys, bool isComposite) {
    Entity = entity;
    SourceEntity = sourceEntity;
    Keys = keys;
    IsComposite = isComposite;
  }

  /// <summary>Gets the entity a row of the keys is a row of, with one member per key.</summary>
  public ILinqEntity Entity { get; }

  /// <inheritdoc/>
  public string Name => Entity.Name;

  /// <inheritdoc/>
  public IReadOnlyList<ILinqEntityMember> Members => Entity.Members;

  /// <inheritdoc/>
  public IReadOnlyList<ILinqEntityMember> PrimaryKeyMembers => Entity.PrimaryKeyMembers;

  /// <inheritdoc/>
  public bool IsTransient => Entity.IsTransient;
  /// <inheritdoc/>
  public bool IsCached => Entity.IsCached;
  public bool IsReadOnly => true;

  /// <inheritdoc/>
  public ILinqEntityMember? FindMember(string name) => Entity.FindMember(name);

  /// <inheritdoc/>
  public override string ToString() => Name;

  /// <summary>Gets the entity of the rows that were grouped, which an aggregate over the group is written over.
  /// </summary>
  public ILinqEntity SourceEntity { get; }

  /// <summary>Gets the key expressions, in the order the selector named them, one per member of the shape.</summary>
  public IReadOnlyList<QueryNode> Keys { get; }

  /// <summary>Gets a value that indicates whether the key selector is a <c>new { … }</c>, whose parts are read off
  /// the key by member, whatever their number.</summary>
  /// <value><see langword="true"/> if <c>g.Key</c> is the group row, read by member; <see langword="false"/> if
  /// it is the one value the selector selects.</value>
  public bool IsComposite { get; }

  /// <summary>Makes the shape a key selector over rows makes: one member per key, named as the selector named it.
  /// </summary>
  /// <param name="model">The model that makes the entity.</param>
  /// <param name="source">The entity of the rows grouped.</param>
  /// <param name="keySelector">The key selector.</param>
  /// <returns>The shape.</returns>
  public static GroupShape Make(ILinqDataModel model, ILinqEntity? source, LambdaNode keySelector) {
    ArgumentNullException.ThrowIfNull(model);
    ArgumentNullException.ThrowIfNull(source);
    var body = keySelector.Body as NewNode;
    var slots = body != null ? body.Children : [keySelector.Body];
    var names = body?.SlotNames;
    var members = new LinqMemberSpec[slots.Count];
    for (var i = 0; i < members.Length; i++) {
      var name = names?[i] ?? (slots[i] as MemberAccessNode)?.MemberName ?? slots[i].Name
        ?? throw new NotSupportedException($"Key {i} of the grouping has no name to take — give it one.");
      // A reference key has no scalar type; the transform reads the key expression, not the field.
      members[i] = new LinqMemberSpec(name, slots[i].DataType ?? typeof(object));
    }
    return new GroupShape(model.CreateTransientEntity(source.Name + "Group", members), source, slots, body != null);
  }
}

/// <summary>Represents what a projection holding whole rows produces rows of, such as the pair a <c>SelectMany</c> or
/// a <c>Join</c> hands on, or a <c>let</c>: a renaming of what is in scope, where a member named off it is the slot's
/// own expression, read in place.</summary>
/// <remarks>The bundle never reaches the SQL tree: binding answers a member named off it with the slot, and the
/// transform substitutes the slot's built value. Returned as the result, it is a projection of whole rows, which the
/// reader materializes from one row of columns.</remarks>
public sealed class BundleShape : ILinqEntity {
  private BundleShape(ILinqEntity entity, IReadOnlyList<QueryNode> slots) {
    Entity = entity;
    Slots = slots;
  }

  /// <summary>Gets the entity a row of the bundle is a row of, with one member per slot.</summary>
  public ILinqEntity Entity { get; }

  /// <inheritdoc/>
  public string Name => Entity.Name;

  /// <inheritdoc/>
  public IReadOnlyList<ILinqEntityMember> Members => Entity.Members;

  /// <inheritdoc/>
  public IReadOnlyList<ILinqEntityMember> PrimaryKeyMembers => Entity.PrimaryKeyMembers;

  /// <inheritdoc/>
  public bool IsTransient => Entity.IsTransient;
  /// <inheritdoc/>
  public bool IsCached => Entity.IsCached;
  public bool IsReadOnly => true;

  /// <inheritdoc/>
  public ILinqEntityMember? FindMember(string name) => Entity.FindMember(name);

  /// <inheritdoc/>
  public override string ToString() => Name;

  /// <summary>Gets the slot expressions, in the order the selector named them, one per member of the shape: a lambda
  /// parameter for a row handed on whole, any expression otherwise.</summary>
  public IReadOnlyList<QueryNode> Slots { get; }

  /// <summary>Makes a bundle shape with one member per slot.</summary>
  /// <param name="model">The model that makes the entity.</param>
  /// <param name="name">The name of the entity.</param>
  /// <param name="slots">The slot expressions, each named.</param>
  /// <returns>The shape.</returns>
  public static BundleShape Make(ILinqDataModel model, string name, IReadOnlyList<QueryNode> slots) {
    ArgumentNullException.ThrowIfNull(model);
    var members = new LinqMemberSpec[slots.Count];
    for (var i = 0; i < members.Length; i++)
      members[i] = new LinqMemberSpec(slots[i].Name!, slots[i].DataType ?? typeof(object));
    return new BundleShape(model.CreateTransientEntity(name, members), slots);
  }

  /// <summary>Returns the position of the slot with a name.</summary>
  /// <param name="member">The name of the slot.</param>
  /// <returns>The zero-based index of the slot.</returns>
  public int IndexOf(string member) {
    for (var i = 0; i < Slots.Count; i++)
      if (string.Equals(Slots[i].Name, member, StringComparison.Ordinal))
        return i;
    throw new NotSupportedException($"'{member}' is not a slot of the projection '{Name}'.");
  }

  /// <summary>Gets the slot with a name.</summary>
  /// <param name="member">The name of the slot.</param>
  /// <returns>The slot expression.</returns>
  public QueryNode GetSlot(string member) => Slots[IndexOf(member)];

  /// <summary>Gets the slot a member access reads, where what it reads from is a bundle.</summary>
  /// <param name="member">The member access.</param>
  /// <returns>The slot expression, or <see langword="null"/> if the member is not read from a bundle.</returns>
  public static QueryNode? FindSlot(MemberAccessNode member) =>
    member.Target?.GetEntity() is BundleShape bundle ? bundle.GetSlot(member.MemberName) : null;
}
