namespace Mic.Orm.Model;

/// <summary>Provides readings off the entity model that more than one place needs and no model class owns.</summary>
public static class EntityModelHelper {
  /// <summary>Gets the members of a key, expanded to the columns that hold it, without the sort directions.</summary>
  /// <param name="key">The key.</param>
  /// <returns>The expanded members, in the key's order.</returns>
  public static IReadOnlyList<EntityMemberInfo> GetMembers(this EntityKeyInfo key) =>
    key.MembersExpanded.Select(km => km.Member).ToArray();

  /// <summary>Gets the reference, on the target or the link entity, that points back at a list member's owner.
  /// </summary>
  /// <param name="member">The list member.</param>
  /// <returns>The reference back to the owner.</returns>
  public static ReferenceInfo GetOwnerReference(this EntityMemberInfo member) =>
    member.ListInfo?.InverseMember?.ReferenceInfo
      ?? throw new ArgumentException($"'{member.Entity?.Name}.{member.Name}' is not a list with a reference back to its owner.", nameof(member));

  /// <summary>Gets the members on the target or the link entity that hold a list member's owner key, in the key's
  /// order.</summary>
  /// <param name="member">The list member.</param>
  /// <returns>The members that hold the owner's key.</returns>
  public static IReadOnlyList<EntityMemberInfo> GetOwnerMembers(this EntityMemberInfo member) =>
    member.GetOwnerReference().FromKey.GetMembers();
}
