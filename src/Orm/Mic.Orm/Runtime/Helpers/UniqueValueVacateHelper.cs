using Mic.Linq;
using Mic.Linq.Nodes;
using Mic.Linq.Translation;
using Mic.Orm.Model;

namespace Mic.Orm.Runtime;

/// <summary>Finds the rows of a save that hold a unique value another record of the save takes, and makes the
/// statements that move those values out of the way before anything else is written.</summary>
/// <remarks><para>A save inserts before it updates and updates before it deletes. A row that is deleted, or changes a
/// unique value, still holds the value when a new row or another update takes it, and the server refuses. The row
/// that frees the value is given a placeholder in a statement of its own at the start of the save; the save then
/// runs in its usual order, and the row is deleted or gets its real value where it always did.</para>
/// <para>A placeholder is made for a string column of <see cref="MinStringSize"/> characters or more, a Guid column
/// and a non-negative integer column; it replaces the whole value. A foreign key column is never given one. A key
/// with no such column, a key over a filtered index and a key value with a <see langword="null"/> in it are left to
/// the server, and so is the primary key.</para></remarks>
internal static class UniqueValueVacateHelper {
  /// <summary>The least declared size of a string column that is given a placeholder; a shorter one has no room for
  /// a value that is unique in practice.</summary>
  public const int MinStringSize = 12;

  /// <summary>The character a string placeholder starts with.</summary>
  public const char PlaceholderMark = '~';

  /// <summary>Makes the statements that vacate every unique value the save frees and takes again.</summary>
  /// <param name="records">The records the save writes.</param>
  /// <param name="tracker">The session's tracker, which holds the values changed columns had in their rows.</param>
  /// <returns>One update per row and key to vacate, scheduled at the start of the save's transaction; empty for a
  /// save with no such value.</returns>
  public static IReadOnlyList<ScheduledMutation> BuildVacates(IReadOnlyList<EntityRecord> records,
      EntityTracker tracker) {
    // A value is freed by a delete or by a change the tracker kept the original of; without either, nothing is.
    if (records.Count < 2 || !tracker.HasOriginals && !records.Any(r => r.Status == EntityStatus.Deleted))
      return [];

    List<ScheduledMutation>? vacates = null;
    foreach (var group in records.GroupBy(r => r.Entity)) {
      var ofEntity = group.ToList();
      if (ofEntity.Count < 2)
        continue;
      foreach (var key in group.Key.Keys) {
        if (!key.IsUnique || key.IsPrimaryKey || key.Index?.Filter != null)
          continue;
        foreach (var freeing in FindFreeingRecords(ofEntity, key, tracker))
          if (BuildVacate(freeing, key, tracker) is { } vacate)
            (vacates ??= []).Add(vacate);
      }
    }
    return vacates ?? [];
  }

  /// <summary>Finds the records whose rows hold a value of the key that another record of the save takes.</summary>
  private static List<EntityRecord> FindFreeingRecords(List<EntityRecord> records, EntityKeyInfo key,
      EntityTracker tracker) {
    Dictionary<EntityKey, EntityRecord>? freed = null;
    foreach (var record in records) {
      if (record.Status is not (EntityStatus.Deleted or EntityStatus.Modified))
        continue;
      var rowValues = GetRowValues(record, key, tracker, out var changed);
      // A modified row that keeps its key values frees nothing.
      if (rowValues == null || record.Status == EntityStatus.Modified && !changed)
        continue;
      (freed ??= [])[new EntityKey(key, rowValues)] = record;
    }

    var freeing = new List<EntityRecord>();
    if (freed == null)
      return freeing;
    foreach (var record in records) {
      if (!TakesValue(record, key) || GetCurrentValues(record, key) is not { } taken)
        continue;
      if (freed.TryGetValue(new EntityKey(key, taken), out var holder) && holder != record && !freeing.Contains(holder))
        freeing.Add(holder);
    }
    return freeing;
  }

  /// <summary>Determines whether a record's statement writes a value of the key: an insert, or an update of one of
  /// the key's columns.</summary>
  private static bool TakesValue(EntityRecord record, EntityKeyInfo key) {
    if (record.Status == EntityStatus.New)
      return true;
    if (record.Status != EntityStatus.Modified)
      return false;
    foreach (var keyMember in key.MembersExpanded)
      if (record.Modified.IsSet(keyMember.Member.DataIndex))
        return true;
    return false;
  }

  /// <summary>Gets the key values a record's row holds in the database: the original of a changed column, the
  /// current value of the rest; <see langword="null"/> if one of them is <see langword="null"/>.</summary>
  private static object?[]? GetRowValues(EntityRecord record, EntityKeyInfo key, EntityTracker tracker,
      out bool changed) {
    changed = false;
    var members = key.MembersExpanded;
    var values = new object?[members.Count];
    for (var i = 0; i < values.Length; i++) {
      var member = members[i].Member;
      var current = record.GetRawValue(member);
      if (tracker.TryGetOriginal(record, member, out var original)) {
        changed |= !Equals(original, current);
        values[i] = original;
      } else {
        values[i] = current;
      }
      if (values[i] == null)
        return null;
    }
    return values;
  }

  /// <summary>Gets the key values a record has now; <see langword="null"/> if one of them is
  /// <see langword="null"/>.</summary>
  private static object?[]? GetCurrentValues(EntityRecord record, EntityKeyInfo key) {
    var members = key.MembersExpanded;
    var values = new object?[members.Count];
    for (var i = 0; i < values.Length; i++)
      if ((values[i] = record.GetRawValue(members[i].Member)) == null)
        return null;
    return values;
  }

  /// <summary>Makes the update that gives a row a placeholder in the first column of the key that can take one;
  /// <see langword="null"/> if no column can.</summary>
  private static ScheduledMutation? BuildVacate(EntityRecord record, EntityKeyInfo key, EntityTracker tracker) {
    // The row is named by its primary key, column by column; a key over a reference is not named this way.
    var primaryKey = record.Entity.PrimaryKey;
    if (primaryKey == null || primaryKey.MembersExpanded.Any(m => m.Member.OwnerReference != null))
      return null;
    foreach (var keyMember in key.MembersExpanded) {
      var member = keyMember.Member;
      // A foreign key has to name a row and the primary key names this one, so neither takes a made-up value.
      if (member.OwnerReference != null || member.IsPrimaryKey)
        continue;
      var rowValue = tracker.TryGetOriginal(record, member, out var original) ? original : record.GetRawValue(member);
      if (MakePlaceholder(member, rowValue) is { } placeholder)
        return BuildUpdate(record, primaryKey, member, placeholder);
    }
    return null;
  }

  /// <summary>Makes a value no row holds for a column, by the column's type; <see langword="null"/> for a type or a
  /// size that has none.</summary>
  /// <remarks>A string is the mark and random hexadecimal digits, cut to the column's size. An integer is the
  /// bitwise complement of the row's value, which is negative for a value that is not: -1 for 0, -2 for 1.</remarks>
  private static object? MakePlaceholder(EntityMemberInfo member, object? rowValue) {
    var type = Nullable.GetUnderlyingType(member.DataType) ?? member.DataType;
    if (type == typeof(string)) {
      var unlimited = member.MemberFlags.HasFlag(MemberFlags.Unlimited);
      if (!unlimited && member.Size < MinStringSize)
        return null;
      var text = PlaceholderMark + Guid.NewGuid().ToString("N");
      return unlimited || member.Size >= text.Length ? text : text[..member.Size];
    }
    if (type == typeof(Guid))
      return Guid.NewGuid();
    return rowValue switch {
      int number when number >= 0 => ~number,
      long number when number >= 0 => ~number,
      short number when number >= 0 => (short)~number,
      _ => null,
    };
  }

  /// <summary>Makes the update of one column of one row, named by its primary key, for the start of the save.
  /// </summary>
  private static ScheduledMutation BuildUpdate(EntityRecord record, EntityKeyInfo primaryKey, EntityMemberInfo member,
      object placeholder) {
    // Naming a slot renames the parameter in it, so the placeholder is filed under the member's name.
    var values = new Dictionary<string, object?> { [member.Name] = placeholder };
    var keyMembers = primaryKey.MembersExpanded;
    var mutation = QueryMaker.From(record.Entity)
      .Where(row => {
        var terms = new QueryNode[keyMembers.Count];
        for (var i = 0; i < terms.Length; i++) {
          var keyMember = keyMembers[i].Member;
          var name = "key" + i;
          values[name] = record.GetRawValue(keyMember);
          terms[i] = row.Member(keyMember.Name).Equal(QueryMaker.Parameter(name, keyMember.DataType));
        }
        return terms.Length == 1 ? terms[0] : QueryMaker.And(terms);
      })
      .Select(row => [QueryMaker.Parameter(member.Name, member.DataType).As(member.Name)])
      .Update(record.Entity);
    // The arguments go in the order the tree names its parameters.
    var arguments = mutation.BuildParametersLayout().Select(parameter => values[parameter.Name!]).ToArray();
    return new ScheduledMutation(mutation, arguments, CommandSchedule.TransactionStart);
  }
}
