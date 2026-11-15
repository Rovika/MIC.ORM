using System.Data.Common;
using Mic.Orm.Runtime;

namespace Mic.Orm.Db.Readers;

/// <summary>Defines how the rows of an open reader become a command's result.</summary>
/// <remarks>A result reader must consume what it needs before returning: the executor closes the data reader as soon as
/// the result reader returns, so a result that reads lazily would read from a closed reader.</remarks>
public interface IResultReader {
  /// <summary>Asynchronously reads the rows of an open reader into the command's result.</summary>
  Task<object?> ReadAsync(DataCommand command, DbDataReader reader, CancellationToken cancellation);
}
