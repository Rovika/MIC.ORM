namespace Mic.Orm.Db.Model.Construction;

/// <summary>Completes a db model once it is assembled, with the names every statement writes. This class cannot be
/// inherited.</summary>
public static class DbModelBuilderHelper {

  /// <summary>Fills the quoted and qualified names of every table and column, and each mapped table's insert column
  /// list, using the model's own provider.</summary>
  /// <param name="model">The assembled model.</param>
  /// <remarks>Every way a db model comes into existence calls this, whether built from an entity model or read from
  /// a database; an upgrade writes an introspected model's names into its statements.</remarks>
  public static void CompleteSetup(DbModel model) {
    ArgumentNullException.ThrowIfNull(model);
    var dialect = model.Provider.SqlDialect;
    foreach (var table in model.Tables) {
      table.QuotedName = dialect.Quote(table.Name);
      table.RefName = table.IsTemporary
        ? dialect.GetTempTableRef(table.Schema.Name, table.Name) : dialect.GetTableRef(table);
      foreach (var column in table.Columns)
        column.QuotedName = dialect.Quote(column.Name);

      // The insert list is in member order; a table loaded from the database has no entity and so no insert list.
      if (table.Entity is { } entity) {
        var insertable = new List<DbColumnInfo>();
        foreach (var member in entity.Members)
          if (!member.IsDbGenerated && table.GetColumn(member) is { } column)
            insertable.Add(column);
        table.InsertColumns = insertable;
        table.InsertColumnList = string.Join(", ", insertable.Select(c => c.QuotedName));
      }
    }
  }
}
