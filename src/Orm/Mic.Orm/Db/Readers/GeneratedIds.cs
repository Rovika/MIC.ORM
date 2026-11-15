using System.Data.Common;
using Mic.Orm.Runtime;

namespace Mic.Orm.Db.Readers;

/// <summary>Represents where a batch leaves a record's generated id: the slot's name, a session variable's or an
/// output parameter's, and the parameter itself where the dialect reads the key back through one.</summary>
/// <param name="Name">The slot's name, without the prefix.</param>
/// <param name="Parameter">The output parameter the key arrives in, or <see langword="null"/> where the batch's last
/// statement selects the keys instead.</param>
public sealed record GeneratedIdSlot(string Name, DbParameter? Parameter);

/// <summary>Reads the row a batch's last statement selected, one column per generated-id slot named after it, off
/// whichever of the command's result sets has columns, as the values by column name.</summary>
public sealed class GeneratedIdsReader : IResultReader {
  public static readonly GeneratedIdsReader Instance = new();

  public async Task<object?> ReadAsync(DataCommand command, DbDataReader reader, CancellationToken cancellation) {
    Dictionary<string, object?>? row = null;
    do {
      if (reader.FieldCount > 0 && await command.ReadRowAsync(reader, cancellation)) {
        row = new Dictionary<string, object?>(reader.FieldCount, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < reader.FieldCount; i++)
          row[reader.GetName(i)] = reader.GetValue(i);
      }
    } while (command.RunMode == RunMode.Sync ? reader.NextResult() : await reader.NextResultAsync(cancellation));
    return row;
  }
}
