using System.Data.Common;
using Mic.Linq.ExpressionTree;
using Mic.Orm.Db.Provider;
using Mic.Orm.Model;
using Mic.Orm.Runtime;

namespace Mic.Orm.Db.Readers;

/// <summary>Reads the rows of a statement the application wrote as records of one entity, matching the columns to the
/// entity's members by name and tracking the records through the identity map as a query's are. This class cannot be
/// inherited.</summary>
/// <remarks>The match is made once from the reader's schema: a column is the member its table column maps to, or the
/// member of its own name. A column no member has is ignored; a member no column fills stays unloaded. A statement
/// that does not select the whole primary key is refused by name.</remarks>
public sealed class SqlEntityReader : IResultReader {
  readonly EntitySession _session;
  readonly EntityTypeInfo _entity;

  public SqlEntityReader(EntitySession session, EntityTypeInfo entity) {
    ArgumentNullException.ThrowIfNull(session);
    ArgumentNullException.ThrowIfNull(entity);
    _session = session;
    _entity = entity;
  }

  public async Task<object?> ReadAsync(DataCommand command, DbDataReader reader, CancellationToken cancellation) {
    var (members, columns) = MatchColumns(reader);
    var records = new List<EntityRecord?>();
    while (await command.ReadRowAsync(reader, cancellation))
      records.Add(EntityRecordReader.ReadRecord(_session, reader, _entity, members, 0, columns));
    return records;
  }

  private (IReadOnlyList<EntityMemberInfo> Members, int[] Columns) MatchColumns(DbDataReader reader) {
    var table = _session.Database.DbModel.GetTable(_entity);
    var members = new List<EntityMemberInfo>();
    var columns = new List<int>();
    for (var i = 0; i < reader.FieldCount; i++) {
      var name = reader.GetName(i);
      var member = table?.FindColumn(name)?.Member ?? FindStoredMember(name);
      if (member == null || members.Contains(member))
        continue;
      members.Add(member);
      columns.Add(i);
    }
    if (_entity.PrimaryKey is { } key)
      foreach (var keyMember in key.MembersExpanded)
        if (!members.Contains(keyMember.Member))
          throw new InvalidOperationException(
            $"'{_entity.Name}': the statement does not select the key member '{keyMember.Member.Name}', so its rows cannot be records.");
    return (members, columns.ToArray());
  }

  private EntityMemberInfo? FindStoredMember(string name) {
    foreach (var member in _entity.Members)
      if (member.Kind is MemberKind.Column or MemberKind.Reference
          && string.Equals(member.Name, name, StringComparison.OrdinalIgnoreCase))
        return member;
    return null;
  }
}

/// <summary>Reads the rows of a statement the application wrote, building each as a <typeparamref name="T"/> by name:
/// through a constructor whose parameters are named after the columns, or a parameterless constructor and settable
/// properties.</summary>
/// <typeparam name="T">The type each row is built as.</typeparam>
/// <remarks>The factory is the one a projection's rows are built with (<see cref="ProjectionReadHelper"/>), made once
/// per type. A column is read by name off the driver's reader and converted through the provider to the member's
/// type, such as a Guid out of sixteen bytes or an enum out of its integer. A constructor parameter with no column of
/// its name is refused by the factory; a settable property with none is left as the constructor made it.</remarks>
public sealed class SqlObjectReader<T> : IResultReader {
  readonly DbProvider _provider;

  public SqlObjectReader(DbProvider provider) {
    ArgumentNullException.ThrowIfNull(provider);
    _provider = provider;
  }

  public async Task<object?> ReadAsync(DataCommand command, DbDataReader reader, CancellationToken cancellation) {
    var create = ProjectionReadHelper.GetFactory<T>();
    var ordinals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < reader.FieldCount; i++)
      ordinals[reader.GetName(i)] = i;
    var values = new object?[reader.FieldCount];
    bool Read(string name, Type wanted, out object? value) {
      if (!ordinals.TryGetValue(name, out var ordinal)) {
        value = null;
        return false;
      }
      value = values[ordinal] == null ? null : _provider.ConvertReadValue(values[ordinal], wanted);
      return true;
    }
    var list = new List<T>();
    while (await command.ReadRowAsync(reader, cancellation)) {
      reader.GetValues(values!);
      for (var i = 0; i < values.Length; i++)
        if (values[i] == DBNull.Value)
          values[i] = null;
      list.Add(create(Read));
    }
    return list;
  }
}
