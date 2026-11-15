using System.Reflection;
using Mic.Common;
using Mic.Linq;
using Mic.Linq.Nodes;
using Mic.Orm.Model;
using Mic.Orm.Runtime;

namespace Mic.Orm.Extensions.MultiTenancy;

/// <summary>Specifies what a save does with a row whose tenant is not the context's.</summary>
public enum TenantBoundaryMode {
  /// <summary>A validation fault, reported with the record's other faults.</summary>
  Refuse,
  /// <summary>An overwrite of the row's tenant with the context's.</summary>
  Enforce,
}

/// <summary>Provides multi-tenancy: an entity bound to a tenant is filtered on every read to the tenant the operation
/// context names under <see cref="TenantIdKey"/>, a new row is stamped with it at save, and a row saved under another
/// tenant is refused or corrected by <see cref="BoundaryMode"/>.</summary>
/// <remarks>The tenant member of an entity is found once, when the model is built, by three rules in order:
/// <c>[TenantId]</c> on a member binds it and <c>[TenantId(false)]</c> excludes the entity; with
/// <see cref="TenantMemberName"/> set, every entity with a member of that name is bound; otherwise an entity carrying
/// <see cref="ITenantBound"/> is bound by its <c>Tenant</c> reference. The member may be a reference or a scalar; the
/// extension works on the column underneath, the reference's foreign key or the scalar itself, whose type is the
/// tenant's type: the filter's parameter is typed after it and the context's value converted to it. Every bound entity
/// has one type, or the model does not build. The filter brings a cache discriminator, the tenant as text, so a cached
/// entity keeps one block per tenant. A context with no tenant sees no rows, and a row it saves with no tenant of its
/// own is refused. The write side runs in each bound entity's <c>ValidatingChanges</c>; a row marked for deletion is not
/// checked. A view entity with no tenant member gets a warning in the build report: it shows every tenant's rows.
/// </remarks>
public class MultiTenantExtension : AppExtension {
  /// <summary>Gets or initializes the operation context's key for the tenant id, as in
  /// <c>context.SetValue(TenantIdKey, tenantId)</c>, which is also the name of the filter's parameter.</summary>
  /// <value>The key. The default is <c>"TenantId"</c>.</value>
  public string TenantIdKey { get; init; } = "TenantId";

  /// <summary>Gets the name of the member that holds the tenant, for a database the extension did not shape: every
  /// entity with a member of that name is bound.</summary>
  /// <value>The member's name, or <see langword="null"/> when the entities are bound by <see cref="ITenantBound"/>.
  /// </value>
  public string? TenantMemberName { get; }

  /// <summary>Gets or initializes the code of the validation fault a row of another tenant, or of none, is refused
  /// with.</summary>
  /// <value>The fault code. The default is <c>"TenantBoundary"</c>.</value>
  public string BoundaryFaultCode { get; init; } = "TenantBoundary";

  /// <summary>Gets or initializes what a save does with a row whose tenant is not the context's.</summary>
  /// <value>One of the enumeration values that specifies the boundary mode. The default is
  /// <see cref="TenantBoundaryMode.Refuse"/>.</value>
  public TenantBoundaryMode BoundaryMode { get; init; } = TenantBoundaryMode.Refuse;

  /// <summary>Gets the type of the tenant, the type of the column every bound entity holds it in, or
  /// <see langword="null"/> before the model is built or when no entity is bound.</summary>
  public Type? TenantType { get; private set; }

  readonly Dictionary<EntityTypeInfo, TenantBinding> _bindings = [];
  object? _noTenant;

  /// <summary>Initializes a new instance of the <see cref="MultiTenantExtension"/> class for a new application: an
  /// entity carrying <see cref="ITenantBound"/> is bound through its <c>Tenant</c> reference to <see cref="ITenant"/>.
  /// </summary>
  /// <remarks>Nothing to configure. The application registers <see cref="ITenant"/> in one of its modules and
  /// inherits <see cref="ITenantBound"/> on its bound entities; the tenant's type is <c>short</c>, the type of
  /// <see cref="ITenant.Id"/>. <c>[TenantId]</c> on a member still binds it, and <c>[TenantId(false)]</c> still
  /// excludes an entity.</remarks>
  public MultiTenantExtension() { }

  /// <summary>Initializes a new instance of the <see cref="MultiTenantExtension"/> class for an existing database:
  /// every entity with a member of the given name is bound through that member.</summary>
  /// <param name="tenantMemberName">The name of the member that holds the tenant on every bound entity: a scalar
  /// code or id, or a reference to the tenant's entity, such as <c>"CompanyCode"</c>.</param>
  /// <remarks>No marker interface is needed, and <see cref="ITenantBound"/> is not consulted; the tenant's type is
  /// the member's, read off the model. <c>[TenantId]</c> on a member binds it under another name, and
  /// <c>[TenantId(false)]</c> excludes an entity that has the member.</remarks>
  public MultiTenantExtension(string tenantMemberName) {
    ArgumentException.ThrowIfNullOrWhiteSpace(tenantMemberName);
    TenantMemberName = tenantMemberName;
  }

  /// <summary>Gets the tenant an operation context names under <see cref="TenantIdKey"/>.</summary>
  /// <param name="context">The operation context.</param>
  /// <returns>The tenant id, or <see langword="null"/> if the context names none.</returns>
  public object? GetTenantId(OperationContext context) {
    ArgumentNullException.ThrowIfNull(context);
    return context.TryGetValue(TenantIdKey, out var value) ? value : null;
  }

  public override void OnEntityBuilt(EntityTypeInfo entity) {
    ArgumentNullException.ThrowIfNull(entity);
    var member = FindTenantMember(entity);
    if (member == null) {
      if (entity.IsView)
        AddWarning($"'{entity.Name}' reads through a view and has no tenant member: it shows every tenant's rows.",
          $"extension '{GetType().Name}', entity '{entity.Name}'");
      return;
    }
    var binding = MakeBinding(entity, member);
    _bindings[entity] = binding;
    entity.AddFilter(QueryMaker.MakeFilter(entity, row => BuildCondition(row, binding)),
      context => GetTenantId((OperationContext)context!)?.ToString() ?? "", GetType());
  }

  public override void OnModelConstructed(EntityAppModel model) {
    foreach (var (entity, binding) in _bindings)
      entity.Events.ValidatingChanges += record => CheckTenant(record, binding);
  }

  /// <summary>Finds the member that holds an entity's tenant: by <c>[TenantId]</c>, by name, or by the marker.
  /// </summary>
  /// <returns>The member, or <see langword="null"/> if the entity is not tenant-bound.</returns>
  private EntityMemberInfo? FindTenantMember(EntityTypeInfo entity) {
    EntityMemberInfo? marked = null;
    foreach (var member in entity.Members) {
      if (member.Property?.GetCustomAttribute<TenantIdAttribute>() is not { } attribute)
        continue;
      if (!attribute.IsTenantId)
        return null;
      if (marked != null)
        throw new InvalidOperationException(
          $"'{entity.Name}' marks both '{marked.Name}' and '{member.Name}' with [TenantId].");
      marked = member;
    }
    if (marked != null)
      return marked;
    if (TenantMemberName != null)
      return entity.FindMember(TenantMemberName);
    return entity.InterfaceType != null && typeof(ITenantBound).IsAssignableFrom(entity.InterfaceType)
      ? entity.FindMember(nameof(ITenantBound.Tenant))
      : null;
  }

  /// <summary>Resolves the column the tenant is held in, a reference's foreign key or the scalar itself, and checks
  /// its type against the other bound entities'.</summary>
  private TenantBinding MakeBinding(EntityTypeInfo entity, EntityMemberInfo member) {
    EntityMemberInfo column;
    EntityMemberInfo? targetKey = null;
    switch (member.Kind) {
      case MemberKind.Column:
        column = member;
        break;
      case MemberKind.Reference:
        var reference = member.ReferenceInfo
          ?? throw new InvalidOperationException($"'{entity.Name}.{member.Name}' is a reference with no target.");
        if (reference.FromKey.IsComposite)
          throw new InvalidOperationException(
            $"'{entity.Name}.{member.Name}' references a composite key; a tenant reference has one key column.");
        column = reference.FromKey.MembersExpanded[0].Member;
        targetKey = reference.ToKey.MembersExpanded[0].Member;
        break;
      default:
        throw new InvalidOperationException(
          $"'{entity.Name}.{member.Name}' is a {member.Kind}; a tenant member is a column or a reference.");
    }
    var type = Nullable.GetUnderlyingType(column.DataType) ?? column.DataType;
    if (TenantType == null) {
      TenantType = type;
      _noTenant = type.IsValueType ? Activator.CreateInstance(type) : null;
    } else if (TenantType != type)
      throw new InvalidOperationException(
        $"'{entity.Name}.{member.Name}' holds the tenant as {type.Name}, and the entities bound before it as "
        + $"{TenantType.Name}; one extension has one tenant type.");
    return new TenantBinding(member, column, targetKey);
  }

  /// <summary>Builds the condition every read of the entity carries: the tenant column equal to a parameter that
  /// reads the context when the statement is bound.</summary>
  private QueryNode BuildCondition(RowNode row, TenantBinding binding) {
    var parameter = QueryMaker.Parameter(TenantIdKey, TenantType!, call => {
      var tenant = ReadContextTenant((OperationContext)call.OperationContext!);
      return HasTenant(tenant) ? tenant : null;
    });
    var tenantNode = binding.TargetKey == null
      ? row.Member(binding.Member.Name)
      : row.Member(binding.Member.Name).Member(binding.TargetKey.Name);
    return tenantNode.Equal(parameter);
  }

  /// <summary>Reads the context's tenant converted to the tenant type.</summary>
  private object? ReadContextTenant(OperationContext context) =>
    ConvertHelper.ConvertValue(GetTenantId(context), TenantType!);

  /// <summary>Determines whether a value names a tenant: anything but null or the tenant type's default.</summary>
  private bool HasTenant(object? value) => value != null && !Equals(value, _noTenant);

  /// <summary>Stamps a new or changed row with the context's tenant where it has none, refuses or corrects one that
  /// has another's, and refuses one where the context has none to give. A session that disabled the extension saves
  /// the row with the tenant it has, or none.</summary>
  private void CheckTenant(EntityRecord record, TenantBinding binding) {
    if (record.Session is { } saving && IsDisabledBy(saving))
      return;
    var tenant = record.Session is { } session ? ReadContextTenant(session.Context) : null;
    var current = ConvertHelper.ConvertValue(record.GetRawValue(binding.Column), TenantType!);
    if (!HasTenant(tenant)) {
      if (!HasTenant(current))
        record.AddValidationFault(new ClientFault(record, BoundaryFaultCode, binding.Member.Name,
          $"'{record.Entity.Name}' belongs to a tenant, and neither the row nor the operation context names one."));
      return;
    }
    if (!HasTenant(current) || Equals(current, tenant) || BoundaryMode == TenantBoundaryMode.Enforce)
      record.SetValue(binding.Column, tenant);
    else
      record.AddValidationFault(new ClientFault(record, BoundaryFaultCode, binding.Member.Name,
        $"'{record.Entity.Name}' belongs to tenant {current}, and the operation is tenant {tenant}'s."));
  }

  /// <summary>How a bound entity holds its tenant: the member found, the column underneath, and for a reference the
  /// target's key member the condition reaches through.</summary>
  private sealed record TenantBinding(EntityMemberInfo Member, EntityMemberInfo Column, EntityMemberInfo? TargetKey);
}
