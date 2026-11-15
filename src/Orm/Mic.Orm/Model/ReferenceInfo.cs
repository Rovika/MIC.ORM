using Mic.Linq.Model;
namespace Mic.Orm.Model;

/// <summary>Represents a foreign key relationship: the pairing of the referencing entity's key with the key it
/// references.</summary>
/// <remarks>The referencing entity's own columns are an ordinary key in its key collection, marked as a foreign key
/// and named for the constraint.</remarks>
public class ReferenceInfo : ILinqReferenceInfo {
  public ReferenceInfo(EntityKeyInfo fromKey, EntityKeyInfo toKey) {
    FromKey = fromKey;
    ToKey = toKey;
  }

  /// <summary>Gets the key on the entity that holds the reference: its foreign key columns and the constraint's name.
  /// </summary>
  public EntityKeyInfo FromKey { get; }

  /// <summary>Gets the primary or unique key on the target that is referenced.</summary>
  public EntityKeyInfo ToKey { get; }

  public EntityTypeInfo TargetEntity => ToKey.Entity;

  // The query engine's view: the two keys expanded to columns, which are what a join compares.
  ILinqEntity ILinqReferenceInfo.TargetEntity => TargetEntity;

  IReadOnlyList<ILinqEntityMember> ILinqReferenceInfo.FromMembers => FromKey.GetMembers();

  IReadOnlyList<ILinqEntityMember> ILinqReferenceInfo.ToMembers => ToKey.GetMembers();

  /// <summary>Gets or sets a value that indicates whether the database enforces this relationship with a constraint.
  /// </summary>
  /// <value><see langword="true"/> if the database enforces the relationship; <see langword="false"/> for one the
  /// model knows about and the database is not asked to police. The default is <see langword="true"/>.</value>
  public bool HasDbConstraint { get; set; } = true;

  /// <summary>Gets or sets a value that indicates whether deleting the target takes the referencing rows with it
  /// (<c>ON DELETE CASCADE</c>).</summary>
  public bool CascadeDelete { get; set; }
}
