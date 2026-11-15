using Mic.Linq.Model;
namespace Mic.Orm.Model;

/// <summary>Holds what a list member needs: the other half of a relationship, seen from the parent's end.</summary>
public class ListInfo : ILinqMemberListInfo {
  public ListInfo(EntityTypeInfo targetEntity) { TargetEntity = targetEntity; }

  /// <summary>Gets the entity the list holds; for a many-to-many, the far entity rather than the link.</summary>
  public EntityTypeInfo TargetEntity { get; }

  public ListKind Kind { get; set; } = ListKind.OneToMany;

  /// <summary>Gets or sets the reference member that points back at this entity, where the foreign key lives: on
  /// the target for a one-to-many, on the link entity for a many-to-many.</summary>
  public EntityMemberInfo? InverseMember { get; set; }

  /// <summary>Gets or sets the link entity of a many-to-many list, whose two references the list traverses.</summary>
  /// <remarks>An ordinary entity with an ordinary table, which usually carries data of its own.</remarks>
  public EntityTypeInfo? LinkEntity { get; set; }

  /// <summary>Gets or sets the link's reference to the far entity of a many-to-many list, the second leg of the
  /// traversal.</summary>
  public EntityMemberInfo? OtherMember { get; set; }

  /// <summary>Gets the order the list loads in; never empty for a list whose target has a primary key.</summary>
  /// <remarks>Decided once by the reference builder: <c>[OrderBy]</c> on the list member, else <c>[OrderBy]</c> on
  /// the target entity, else the <see cref="PersistOrderMember"/>, else the target's primary key. A member here may be
  /// a reference; the query expands it to its foreign-key columns.</remarks>
  public IReadOnlyList<KeyMember> OrderBy {
    get => _orderBy;
    internal set { _orderBy = value; _defaultOrder = null; }
  }

  /// <summary>Gets or sets the integer member of the target or the link, from <c>[PersistOrderIn]</c>, that holds the
  /// list's position and is renumbered when the list is saved.</summary>
  /// <value>The member, or <see langword="null"/> for a list whose order is not persisted.</value>
  public EntityMemberInfo? PersistOrderMember { get; set; }

  /// <summary>Gets or sets the runtime list class for this member, <c>MemberEntityList&lt;T&gt;</c> over the target's
  /// interface, made once on the first list a record hands out.</summary>
  internal Type? ListType { get; set; }

  /// <summary>Gets or sets what a query rooted at a member declared as a query contributes to a typed query's cache
  /// key; zero until the first such query is made.</summary>
  internal ulong RowSetCacheKey { get; set; }

  IReadOnlyList<KeyMember> _orderBy = [];
  IReadOnlyList<LinqOrderTerm>? _defaultOrder;

  // The query engine's view.
  ILinqEntity ILinqMemberListInfo.TargetEntity => TargetEntity;

  bool ILinqMemberListInfo.IsManyToMany => Kind == ListKind.ManyToMany;

  ILinqEntityMember? ILinqMemberListInfo.InverseMember => InverseMember;

  ILinqEntity? ILinqMemberListInfo.LinkEntity => LinkEntity;

  ILinqEntityMember? ILinqMemberListInfo.LinkTargetMember => OtherMember;

  IReadOnlyList<LinqOrderTerm> ILinqMemberListInfo.DefaultOrder =>
    _defaultOrder ??= OrderBy.Select(km => new LinqOrderTerm(km.Member, km.Descending)).ToArray();
}
