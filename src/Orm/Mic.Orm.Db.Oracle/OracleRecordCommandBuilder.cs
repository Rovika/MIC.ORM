using System.Data;
using System.Data.Common;
using Mic.Orm.Db.Model;
using Mic.Orm.Db.Provider;
using Mic.Orm.Db.Sql;
using Mic.Orm.Runtime;
using Oracle.ManagedDataAccess.Client;
using Oracle.ManagedDataAccess.Types;

namespace Mic.Orm.Db.Oracle;

/// <summary>Builds the record commands for Oracle, whose <c>RETURNING … INTO :p</c> hands a generated id back in an
/// output parameter rather than in the statement's result.</summary>
/// <remarks>Inside a batch, which is a PL/SQL block, a bind set by one statement's <c>RETURNING … INTO</c> is read by
/// the next statement that names it, and the client reads every bind once the block has run; the dialect's
/// <c>BatchCaptureIdentity</c> stays empty.</remarks>
public class OracleRecordCommandBuilder(DbModel dbModel) : RecordCommandBuilder(dbModel) {
  const string ReturnedKey = "micGeneratedId";

  public override DataCommand BuildInsert(EntityRecord record) {
    var command = base.BuildInsert(record);
    // In a batch that carries keys the base already wrote the clause through ConfigureIdValueReturn.
    if (CarriesGeneratedIds || !ShouldReturnId(record) || record.Entity.IdentityMember is not { } identity
        || GetTable(record).GetColumn(identity) is not { } column)
      return command;

    // The base produced a plain INSERT, since the dialect declares no returning template: add the clause and the
    // parameter it names, and take the result off the parameter.
    command.DbCommand.Parameters.Add(CreateKeyParameter(command.DbCommand, ReturnedKey));
    command.DbCommand.CommandText +=
      $" RETURNING {column.QuotedName} INTO {Provider.ParameterPrefix}{ReturnedKey}";

    // NonQuery, not Scalar: the statement returns no row; the value arrives on the parameter once it has run.
    return new DataCommand(command.DbCommand, DbExecutionType.NonQuery, command.Description) {
      Record = record,
    };
  }

  /// <summary>Appends <c>RETURNING … INTO :idN</c> to an insert and registers the bind as the key's slot, which the
  /// statements after it read by name.</summary>
  /// <param name="dbCommand">The command the batch is built into.</param>
  /// <param name="record">The record being inserted.</param>
  /// <param name="identityColumn">The identity column whose value the server generates.</param>
  /// <param name="insert">The insert statement without the clause.</param>
  /// <returns>The insert statement with the clause.</returns>
  protected override string ConfigureIdValueReturn(DbCommand dbCommand, EntityRecord record, DbColumnInfo identityColumn, string insert) {
    var name = NextIdSlotName();
    var parameter = CreateKeyParameter(dbCommand, name);
    dbCommand.Parameters.Add(parameter);
    RegisterIdSlot(record, name, parameter);
    return $"{insert} RETURNING {identityColumn.QuotedName} INTO {GetIdSlotReference(name)}";
  }

  public override void AssignGeneratedId(DataCommand command) {
    if (command.BatchRecords != null) {
      base.AssignGeneratedId(command);
      return;
    }
    if (command.Record is not { } record || record.Entity.IdentityMember is not { } identity)
      return;
    if (!command.DbCommand.Parameters.Contains(ReturnedKey))
      return;
    if (ReadIdSlotValue(command.DbCommand.Parameters[ReturnedKey]) is { } value)
      record.SetLoadedValue(identity, Provider.ConvertReadValue(value, identity.DataType));
  }

  /// <summary>Reads a slot's value from its output parameter, which the driver fills with its own
  /// <see cref="OracleDecimal"/>; an unset parameter reads as a null <see cref="OracleDecimal"/>.</summary>
  /// <remarks>The value comes back as a <c>long</c>, the widest the column could hold; the caller converts it to the
  /// member's type.</remarks>
  /// <param name="parameter">The output parameter of the slot.</param>
  /// <returns>The value, or <see langword="null"/> when the parameter was not set.</returns>
  protected override object? ReadIdSlotValue(DbParameter parameter) =>
    ((OracleParameter)parameter).Value is OracleDecimal { IsNull: false } number ? number.ToInt64() : null;

  private static OracleParameter CreateKeyParameter(DbCommand dbCommand, string name) {
    var parameter = (OracleParameter)dbCommand.CreateParameter();
    parameter.ParameterName = name;
    parameter.Direction = ParameterDirection.Output;
    parameter.OracleDbType = OracleDbType.Int64;
    return parameter;
  }
}
