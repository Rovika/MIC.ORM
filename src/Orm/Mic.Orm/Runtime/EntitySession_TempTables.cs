using System.Reflection;
using Mic.Orm.Db;
using Mic.Orm.Db.Model;
using Mic.Orm.Db.Model.Upgrades;
using Mic.Orm.Db.Provider;
using Mic.Orm.Model;

namespace Mic.Orm.Runtime;

// The session's temp tables: registered with their rows, created and filled on the session's connection before the
// first request that follows, dropped when the session ends.
public partial class EntitySession {
  readonly Dictionary<EntityTypeInfo, TempTable> _tempTables = new(ReferenceEqualityComparer.Instance);
  List<TempTable>? _pendingTempTables;
  int _rowsTableCount;

  /// <summary>Gets the next number for a temp table the rows of a parameter are staged in, unique in the session.
  /// </summary>
  internal int NextRowsTableNumber() => ++_rowsTableCount;

  /// <summary>Represents a temp table of the session: its entity and the rows it is filled with.</summary>
  private sealed class TempTable(EntityTypeInfo entity, IReadOnlyList<object> rows) {
    public EntityTypeInfo Entity => entity;
    public IReadOnlyList<object> Rows => rows;
    public bool IsCreated { get; set; }
  }

  /// <summary>Registers a temp table of this session with its rows, to be created and filled before the next
  /// request runs.</summary>
  /// <param name="entity">The entity the module declared as a temp table.</param>
  /// <param name="rows">The rows: objects whose readable properties match the entity's members by name.</param>
  /// <exception cref="ArgumentException">The entity is not a temp table.</exception>
  /// <exception cref="InvalidOperationException">The session has the table already.</exception>
  internal void AddTempTable(EntityTypeInfo entity, IEnumerable<object> rows) {
    if (!entity.IsTempTable)
      throw new ArgumentException($"'{entity.Name}' is not a temp table; a module declares one with RegisterTempTable.", nameof(entity));
    if (_tempTables.ContainsKey(entity))
      throw new InvalidOperationException($"'{entity.Name}' is already created in this session; a temp table is filled once.");
    var table = new TempTable(entity, [.. rows]);
    _tempTables[entity] = table;
    (_pendingTempTables ??= []).Add(table);
  }

  /// <summary>Asynchronously creates and fills the temp tables registered since the last request, on the session's
  /// connection.</summary>
  /// <remarks>The executor calls this at the start of every request. The pending list is taken before anything
  /// runs, since the creating and filling requests come through the executor too.</remarks>
  internal async Task CreatePendingTempTablesAsync() {
    if (_pendingTempTables is not { Count: > 0 } pending)
      return;
    _pendingTempTables = null;
    // A temp table lives as long as its connection, so the connection stays open until the session ends, whatever
    // the context's reuse mode says; the session drops the tables when it is disposed.
    var connection = GetConnection();
    if (connection.Lifetime < DbConnectionLifetime.Explicit)
      connection.Lifetime = DbConnectionLifetime.Explicit;
    var ddl = new DbDdlBuilder(Database.Provider);
    foreach (var tempTable in pending) {
      var table = Database.DbModel.GetTable(tempTable.Entity)
        ?? throw new InvalidOperationException($"'{tempTable.Entity.Name}' has no table in this database.");
      await Database.RequestExecutor.ExecuteAsync(new DataRequest(this, ddl.CreateTempTable(table).Text, [],
        DbExecutionType.NonQuery, description: $"create temp table {tempTable.Entity.Name}"));
      tempTable.IsCreated = true;
      if (tempTable.Rows.Count == 0)
        continue;
      var records = BuildTempRecords(tempTable);
      var fill = BuildBulkFill(table, records) is { } bulk
        ? new DataRequest(this, bulk.Describe(), [], DbExecutionType.NonQuery,
            description: $"fill temp table {tempTable.Entity.Name}") { BulkInsert = bulk }
        : new DataRequest(this, records, description: $"fill temp table {tempTable.Entity.Name}");
      await Database.RequestExecutor.ExecuteAsync(fill);
    }
  }

  /// <summary>Builds the fill of a temp table over the server's bulk channel, or returns <see langword="null"/>
  /// where the server has none or a column takes more than a plain value, as a geographic point does.</summary>
  private BulkInsertInfo? BuildBulkFill(DbTableInfo table, IReadOnlyList<EntityRecord> records) {
    var provider = Database.Provider;
    var columns = table.Columns.Where(c => c.Member is { Kind: MemberKind.Column }).ToList();
    if (!provider.Supports(DbFeatures.BulkInsert) || columns.Any(c => c.IsGeoPoint || c.Member!.IsJson))
      return null;
    var rows = new object?[records.Count][];
    for (var r = 0; r < records.Count; r++) {
      rows[r] = new object?[columns.Count];
      for (var c = 0; c < columns.Count; c++)
        rows[r][c] = records[r].GetRawValue(columns[c].Member!) is { } value
          ? provider.AdaptParameterValue(value) : null;
    }
    return new BulkInsertInfo(table.RefName, [.. columns.Select(c => c.Name)], rows);
  }

  /// <summary>Builds the untracked records the fill inserts, one per row, each member read off the row's property of
  /// its name.</summary>
  private List<EntityRecord> BuildTempRecords(TempTable tempTable) {
    var entity = tempTable.Entity;
    var members = entity.Members.Where(m => m.Kind == MemberKind.Column).ToList();
    var propertiesByType = new Dictionary<Type, PropertyInfo?[]>();
    var records = new List<EntityRecord>(tempTable.Rows.Count);
    foreach (var row in tempTable.Rows) {
      if (!propertiesByType.TryGetValue(row.GetType(), out var properties))
        propertiesByType[row.GetType()] = properties = FindRowProperties(row.GetType(), members, entity);
      var record = new EntityRecord(entity, this) { Status = EntityStatus.New };
      record.MarkFullyLoaded();
      for (var i = 0; i < members.Count; i++) {
        var value = properties[i]?.GetValue(row);
        if (value == null && !members[i].IsNullable)
          throw new InvalidOperationException(
            $"A row for '{entity.Name}' gives nothing for '{members[i].Name}', which is required.");
        record.SetRawValue(members[i], value);
      }
      records.Add(record);
    }
    return records;
  }

  /// <summary>Finds, on a row type, the property of each member's name, ignoring case; a member a row type has no
  /// property for must be nullable.</summary>
  private static PropertyInfo?[] FindRowProperties(Type rowType, IReadOnlyList<EntityMemberInfo> members,
      EntityTypeInfo entity) {
    var properties = new PropertyInfo?[members.Count];
    for (var i = 0; i < members.Count; i++) {
      properties[i] = rowType.GetProperty(members[i].Name,
        BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
      if (properties[i] == null && !members[i].IsNullable)
        throw new InvalidOperationException(
          $"'{rowType.Name}' has no '{members[i].Name}', which '{entity.Name}' requires of every row.");
    }
    return properties;
  }

  /// <summary>Asynchronously drops the temp tables the session created, before its connection goes back to the pool.
  /// </summary>
  private async Task DropTempTablesAsync() {
    if (Connection == null)
      return;
    var ddl = new DbDdlBuilder(Database.Provider);
    foreach (var tempTable in _tempTables.Values) {
      if (!tempTable.IsCreated)
        continue;
      var table = Database.DbModel.GetTable(tempTable.Entity)!;
      await Database.RequestExecutor.ExecuteAsync(new DataRequest(this, ddl.DropTempTable(table).Text, [],
        DbExecutionType.NonQuery, description: $"drop temp table {tempTable.Entity.Name}"));
    }
    _tempTables.Clear();
  }
}
