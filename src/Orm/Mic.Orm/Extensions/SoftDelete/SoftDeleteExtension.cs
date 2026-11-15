using System.Reflection;
using Mic.Linq;
using Mic.Linq.Nodes;
using Mic.Orm.Model;
using Mic.Orm.Runtime;

namespace Mic.Orm.Extensions.SoftDelete;

/// <summary>Defines the marker of an entity whose rows are never removed: a delete stamps <see cref="DeletedOn"/>
/// instead, and the stamped rows are hidden from every read.</summary>
/// <remarks>Inherited by the entity interface; the member is the column. A legacy database whose mark is named
/// otherwise, an <c>IsDeleted</c> bit or a <c>RemovedOn</c> date, configures <see cref="SoftDeleteExtension"/>
/// with the member's name instead.</remarks>
public interface ISoftDeleteEntity {
  DateTime? DeletedOn { get; set; }
}

/// <summary>Specifies that a member is its entity's deleted mark, or, with <see langword="false"/>, that the entity
/// is not soft-deleted although it has a member of the name <see cref="SoftDeleteExtension.DeletedMemberName"/>
/// gives.</summary>
/// <remarks>Read by <see cref="SoftDeleteExtension"/> before its name and its marker rules: an attribute wins.
/// </remarks>
[AttributeUsage(AttributeTargets.Property)]
public sealed class DeletedMarkAttribute : Attribute {
  /// <summary>Initializes a new instance of the <see cref="DeletedMarkAttribute"/> class.</summary>
  /// <param name="isDeletedMark"><see langword="true"/> if the member is the deleted mark; <see langword="false"/>
  /// to exclude the entity.</param>
  public DeletedMarkAttribute(bool isDeletedMark = true) {
    IsDeletedMark = isDeletedMark;
  }

  /// <summary>Gets a value that indicates whether the member is the deleted mark, or the entity is excluded.
  /// </summary>
  public bool IsDeletedMark { get; }
}

/// <summary>Provides soft delete: an entity bound to a deleted mark keeps its rows, a delete becomes an update
/// writing the mark, and a filter on every read hides the marked rows.</summary>
/// <remarks>The deleted member of an entity is found once, when the model is built, by three rules in order:
/// <c>[DeletedMark]</c> on a member binds it and <c>[DeletedMark(false)]</c> excludes the entity; with
/// <see cref="DeletedMemberName"/> set, every entity with a member of that name is bound; otherwise an entity
/// carrying <see cref="ISoftDeleteEntity"/> is bound by its <c>DeletedOn</c>. The member's type says what the mark
/// is: a nullable <see cref="DateTime"/> is <see langword="null"/> while the row lives and takes the application's
/// clock's time at the delete; a <see cref="bool"/> is <see langword="false"/> while the row lives and becomes
/// <see langword="true"/>. Any other type does not build. The filter is the same for every caller, so a cached entity
/// keeps one block. The conversion runs in the application's <c>SavingChanges</c>: a record marked for deletion is
/// set modified and stamped, and within that session it still reads as it did. Reading a deleted row back on purpose
/// is not provided.</remarks>
public class SoftDeleteExtension : AppExtension {
  /// <summary>Gets the name of the member that holds the deleted mark, for a database the extension did not shape:
  /// every entity with a member of that name is bound.</summary>
  /// <value>The member's name, or <see langword="null"/> when the entities are bound by
  /// <see cref="ISoftDeleteEntity"/>.</value>
  public string? DeletedMemberName { get; }

  readonly Dictionary<EntityTypeInfo, DeletedBinding> _bindings = [];

  /// <summary>Initializes a new instance of the <see cref="SoftDeleteExtension"/> class for a new application: an
  /// entity carrying <see cref="ISoftDeleteEntity"/> is bound by its <c>DeletedOn</c>, stamped with the application's
  /// clock at a delete.</summary>
  /// <remarks>Nothing to configure. <c>[DeletedMark]</c> on a member still binds it, and <c>[DeletedMark(false)]</c>
  /// still excludes an entity.</remarks>
  public SoftDeleteExtension() { }

  /// <summary>Initializes a new instance of the <see cref="SoftDeleteExtension"/> class for an existing database:
  /// every entity with a member of the given name is bound by that member.</summary>
  /// <param name="deletedMemberName">The name of the member that holds the deleted mark on every bound entity: a
  /// nullable <see cref="DateTime"/> such as <c>"RemovedOn"</c>, or a <see cref="bool"/> such as
  /// <c>"IsDeleted"</c>.</param>
  /// <remarks>No marker interface is needed, and <see cref="ISoftDeleteEntity"/> is not consulted; what the mark is
  /// comes from the member's type. <c>[DeletedMark]</c> on a member binds it under another name, and
  /// <c>[DeletedMark(false)]</c> excludes an entity that has the member.</remarks>
  public SoftDeleteExtension(string deletedMemberName) {
    ArgumentException.ThrowIfNullOrWhiteSpace(deletedMemberName);
    DeletedMemberName = deletedMemberName;
  }

  public override void OnEntityBuilt(EntityTypeInfo entity) {
    ArgumentNullException.ThrowIfNull(entity);
    if (FindDeletedMember(entity) is not { } member)
      return;
    var binding = MakeBinding(entity, member);
    _bindings[entity] = binding;
    entity.AddFilter(QueryMaker.MakeFilter(entity, row => BuildCondition(row, binding)), owner: GetType());
  }

  public override void OnModelConstructed(EntityAppModel model) => App!.Events.SavingChanges += OnSavingChanges;

  /// <summary>Finds the member that holds an entity's deleted mark: by <c>[DeletedMark]</c>, by name, or by the
  /// marker.</summary>
  /// <returns>The member, or <see langword="null"/> if the entity is not soft-deleted.</returns>
  private EntityMemberInfo? FindDeletedMember(EntityTypeInfo entity) {
    EntityMemberInfo? marked = null;
    foreach (var member in entity.Members) {
      if (member.Property?.GetCustomAttribute<DeletedMarkAttribute>() is not { } attribute)
        continue;
      if (!attribute.IsDeletedMark)
        return null;
      if (marked != null)
        throw new InvalidOperationException(
          $"'{entity.Name}' marks both '{marked.Name}' and '{member.Name}' with [DeletedMark].");
      marked = member;
    }
    if (marked != null)
      return marked;
    if (DeletedMemberName != null)
      return entity.FindMember(DeletedMemberName);
    return entity.InterfaceType != null && typeof(ISoftDeleteEntity).IsAssignableFrom(entity.InterfaceType)
      ? entity.FindMember(nameof(ISoftDeleteEntity.DeletedOn))
      : null;
  }

  /// <summary>Resolves what the mark is from the member's type: a date stamped at the delete, or a flag set.
  /// </summary>
  private static DeletedBinding MakeBinding(EntityTypeInfo entity, EntityMemberInfo member) {
    if (member.Kind != MemberKind.Column)
      throw new InvalidOperationException(
        $"'{entity.Name}.{member.Name}' is a {member.Kind}; a deleted mark is a column.");
    var type = Nullable.GetUnderlyingType(member.DataType) ?? member.DataType;
    if (type == typeof(DateTime) && member.IsNullable)
      return new DeletedBinding(member, IsDate: true);
    if (type == typeof(bool))
      return new DeletedBinding(member, IsDate: false);
    throw new InvalidOperationException(
      $"'{entity.Name}.{member.Name}' is a {(member.IsNullable ? "nullable " : "")}{type.Name}; "
      + "a deleted mark is a nullable DateTime or a bool.");
  }

  /// <summary>Builds the condition every read of the entity carries, selecting the rows not deleted: the date
  /// <see langword="null"/>, or the flag <see langword="false"/>.</summary>
  private static QueryNode BuildCondition(RowNode row, DeletedBinding binding) =>
    row.Member(binding.Member.Name).Equal(binding.IsDate
      ? QueryMaker.Constant(null, binding.Member.DataType)
      : QueryMaker.Constant(false));

  /// <summary>Turns each deletion of a bound entity into a marked update, before the save looks at anything else:
  /// the date set to the application's clock's time, or the flag set. A session that disabled the extension deletes
  /// its rows.</summary>
  private void OnSavingChanges(EntitySession session) {
    if (IsDisabledBy(session))
      return;
    foreach (var record in session.ChangedRecords) {
      if (record.Status != EntityStatus.Deleted || !_bindings.TryGetValue(record.Entity, out var binding))
        continue;
      record.Status = EntityStatus.Modified;
      record.SetValue(binding.Member, binding.IsDate ? App!.TimeService.UtcNow : true);
    }
  }

  /// <summary>How a bound entity holds its deleted mark: the member, and whether it is a date or a flag.</summary>
  private sealed record DeletedBinding(EntityMemberInfo Member, bool IsDate);
}
