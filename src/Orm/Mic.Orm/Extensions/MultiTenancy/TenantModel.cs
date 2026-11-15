namespace Mic.Orm.Extensions.MultiTenancy;

/// <summary>Represents a tenant of a multi-tenant application: the entity every tenant-bound row references.</summary>
/// <remarks>The application registers it in one of its modules, since an extension owns no entities. The id is a
/// 16-bit integer, assigned rather than generated: it repeats in every bound table, so its width is the one choice
/// that costs storage everywhere, and a tenant is created rarely enough to be numbered by hand.</remarks>
[Entity]
public interface ITenant {
  [PrimaryKey] short Id { get; set; }
  [Size(50)] string Name { get; set; }
}

/// <summary>Defines the marker of an entity whose rows belong to a tenant: every read sees the context's tenant's
/// rows and nothing else, and every row written carries it.</summary>
/// <remarks>Inherited by the entity interface. The reference's key column holds the tenant's id, and its index
/// serves the condition every read carries.</remarks>
public interface ITenantBound {
  [Index] ITenant Tenant { get; set; }
}

/// <summary>Specifies that a member holds its entity's tenant, or, with <see langword="false"/>, that the entity
/// is not tenant-bound although it has a member of the name <see cref="MultiTenantExtension.TenantMemberName"/>
/// gives.</summary>
/// <remarks>Read by <see cref="MultiTenantExtension"/> before its name and its marker rules: an attribute wins.
/// </remarks>
[AttributeUsage(AttributeTargets.Property)]
public sealed class TenantIdAttribute : Attribute {
  /// <summary>Initializes a new instance of the <see cref="TenantIdAttribute"/> class.</summary>
  /// <param name="isTenantId"><see langword="true"/> if the member holds the tenant; <see langword="false"/> to
  /// exclude the entity.</param>
  public TenantIdAttribute(bool isTenantId = true) {
    IsTenantId = isTenantId;
  }

  /// <summary>Gets a value that indicates whether the member holds the tenant, or the entity is excluded.</summary>
  public bool IsTenantId { get; }
}
