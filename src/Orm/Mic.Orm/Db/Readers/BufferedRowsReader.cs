using System.Data.Common;
using Mic.Orm.Runtime;

namespace Mic.Orm.Db.Readers;

/// <summary>Reads every row into an <see cref="InfoTable"/>; the result reader a reader command gets when it names
/// none.</summary>
/// <remarks>The whole result set is buffered. A query that returns very many rows should supply a result reader of its
/// own.</remarks>
public sealed class BufferedRowsReader : IResultReader {
  public static readonly BufferedRowsReader Instance = new();

  public async Task<object?> ReadAsync(DataCommand command, DbDataReader reader, CancellationToken cancellation) =>
    await InfoTable.ReadAsync(reader, cancellation, command.RunMode);
}
