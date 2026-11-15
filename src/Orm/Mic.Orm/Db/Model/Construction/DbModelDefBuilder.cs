using Mic.Modeling.Db;
using Mic.Orm.Db.Model.Upgrades;

namespace Mic.Orm.Db.Model.Construction;

/// <summary>Projects a runtime db model, or a diff of two, into the transferrable form the design surface reads.
/// </summary>
/// <remarks>The projection runs one way only; nothing builds a db model from its transferrable form.</remarks>
public class DbModelDefBuilder {

  /// <summary>Projects a runtime db model into its transferrable form.</summary>
  /// <param name="model">The model to project.</param>
  /// <returns>The transferrable form of the model.</returns>
  public DbModelDef FromModel(DbModel model) {
    ArgumentNullException.ThrowIfNull(model);
    var def = new DbModelDef(model.Name, model.Provider.ServerKind) {
      AppName = model.EntityModel?.Name,
      Version = model.EntityModel?.Version.ToString(),
    };

    foreach (var schema in model.Schemas)
      def.Schemas.Add(new DbSchemaDef(schema.Name) {
        // A schema with no entity schema behind it is managed: a schema MIC built is MIC's to change.
        IsManaged = schema.EntitySchema?.IsManaged ?? true,
      });

    foreach (var sequence in model.Sequences)
      def.Sequences.Add(new DbSequenceDef(sequence.Name, sequence.Schema.Name) {
        DataType = sequence.DataType.Name,
        StartValue = sequence.StartValue ?? 1,
        Increment = sequence.Increment,
      });

    // A model built from entities holds a view and a function's rows as tables. A definition lists each under its
    // own kind, as one read from a server does; a temp table is a session's and no part of a database.
    foreach (var table in model.Tables) {
      if (table.IsTemporary)
        continue;
      if (table.View is { } tableView)
        def.Views.Add(FromView(tableView, table.Columns, table.Comment));
      else if (table.TableFunction is { } function)
        def.Routines.Add(FromTableFunction(table, function));
      else
        def.Tables.Add(FromTable(table));
    }

    def.LoadedParts = model.LoadedParts;

    foreach (var view in model.Views)
      def.Views.Add(FromView(view, view.Columns, view.Comment));
    foreach (var routine in model.Routines)
      def.Routines.Add(FromRoutine(routine));
    foreach (var package in model.Packages)
      def.Packages.Add(FromPackage(package));
    foreach (var customType in model.CustomTypes)
      def.CustomTypes.Add(FromCustomType(customType));
    foreach (var synonym in model.Synonyms)
      def.Synonyms.Add(new DbSynonymDef(synonym.Name, synonym.Schema.Name) {
        TargetSchema = synonym.TargetSchema,
        TargetName = synonym.TargetName,
        TargetDatabase = synonym.TargetDatabase,
        Comment = synonym.Comment,
      });

    return def;
  }

  private DbViewDef FromView(DbViewInfo view, IReadOnlyList<DbColumnInfo> columns, string? comment) {
    var def = new DbViewDef(view.Name, view.Schema.Name) {
      IsMaterialized = view.IsMaterialized,
      Source = view.Source,
      Comment = comment,
    };
    foreach (var column in columns)
      def.Columns.Add(FromColumn(column));
    return def;
  }

  private DbRoutineDef FromTableFunction(DbTableInfo table, DbTableFunctionInfo function) {
    var def = new DbRoutineDef(table.Name, table.Schema.Name) {
      RoutineKind = DbRoutineKind.TableFunction,
      Source = function.Body,
      Comment = table.Comment,
      ResultColumns = table.Columns.Select(FromColumn).ToList(),
    };
    foreach (var parameter in function.Parameters)
      def.Parameters.Add(FromParameter(parameter));
    return def;
  }

  private static DbParameterDef FromParameter(DbParameterInfo parameter) =>
    new(parameter.Name) {
      TypeSpec = parameter.TypeSpec,
      Direction = parameter.Direction,
      Position = parameter.Position,
      HasDefault = parameter.HasDefault,
      DefaultText = parameter.DefaultText,
    };

  private DbRoutineDef FromRoutine(DbRoutineInfo routine) {
    var def = new DbRoutineDef(routine.Name, routine.Schema.Name) {
      RoutineKind = routine.RoutineKind,
      ReturnTypeSpec = routine.ReturnTypeSpec,
      Source = routine.Source,
      Comment = routine.Comment,
      // Null means the server cannot say what the routine returns; empty means it returns no rows.
      ResultColumns = routine.ResultColumns?.Select(FromColumn).ToList(),
    };
    foreach (var parameter in routine.Parameters)
      def.Parameters.Add(FromParameter(parameter));
    return def;
  }

  private DbPackageDef FromPackage(DbPackageInfo package) {
    var def = new DbPackageDef(package.Name, package.Schema.Name) {
      Source = package.Source,
      BodySource = package.BodySource,
      Comment = package.Comment,
    };
    foreach (var routine in package.Routines)
      def.Routines.Add(FromRoutine(routine));
    return def;
  }

  private DbCustomTypeDef FromCustomType(DbCustomTypeInfo customType) {
    var def = new DbCustomTypeDef(customType.Name, customType.Schema.Name) {
      TypeKind = customType.TypeKind,
      BaseTypeSpec = customType.BaseTypeSpec,
      IsNullable = customType.IsNullable,
      CheckExpression = customType.CheckExpression,
      Comment = customType.Comment,
      EnumValues = customType.EnumValues.ToList(),
    };
    foreach (var attribute in customType.Attributes)
      def.Attributes.Add(FromColumn(attribute));
    return def;
  }

  private DbTableDef FromTable(DbTableInfo table) {
    var def = new DbTableDef(table.Name, table.Schema.Name);

    foreach (var column in table.Columns)
      def.Columns.Add(FromColumn(column));

    foreach (var key in table.Keys) {
      var keyDef = new DbKeyDef(key.Name, key.Flags) { Filter = key.Filter, IndexType = key.IndexType };
      foreach (var column in key.Columns)
        keyDef.Columns.Add(new DbKeyColumnDef(column.Column.Name, column.Descending));
      foreach (var column in key.IncludedColumns)
        keyDef.IncludedColumns.Add(column.Name);
      def.Keys.Add(keyDef);
    }

    foreach (var constraint in table.RefConstraints)
      def.RefConstraints.Add(new DbRefConstraintDef(constraint.Name, constraint.FromKey.Name,
          constraint.ToTable.Schema.Name, constraint.ToTable.Name, constraint.ToKey.Name) {
        CascadeDelete = constraint.CascadeDelete,
      });

    foreach (var trigger in table.Triggers)
      def.Triggers.Add(new DbTriggerDef(trigger.Name) {
        Timing = trigger.Timing,
        Events = trigger.Events,
        IsRowLevel = trigger.IsRowLevel,
        IsEnabled = trigger.IsEnabled,
        Source = trigger.Source,
      });

    foreach (var check in table.CheckConstraints)
      def.CheckConstraints.Add(new DbCheckConstraintDef(check.Name) { Expression = check.Expression });

    def.Comment = table.Comment;
    return def;
  }

  /// <summary>Projects a column of a table, a view, a routine's result or a composite type.</summary>
  private static DbColumnDef FromColumn(DbColumnInfo column) =>
    new(column.Name, column.TypeSpec) {
      ClrTypeName = column.TypeRef.ClrType?.Name,
      IsNullable = column.IsNullable,
      Flags = column.DbFlags,
      Size = column.Size,
      Precision = column.Precision,
      Scale = column.Scale,
      DefaultExpression = column.DefaultExpression,
      DefaultConstraintName = column.DefaultConstraintName,
      Comment = column.Comment,
    };

  // ----- the diff ---------------------------------------------------------------------------------------------------

  /// <summary>Projects a diff into its transferrable form, optionally with the statements of the script that
  /// implements it attached to the differences they came from.</summary>
  /// <param name="diff">The diff to project.</param>
  /// <param name="script">The script built from the diff, or <see langword="null"/> to project the differences
  /// alone.</param>
  /// <returns>The transferrable form of the diff.</returns>
  public DbModelDiff FromDiff(DbModelDiffInfo diff, DbUpgradeScript? script = null) {
    ArgumentNullException.ThrowIfNull(diff);
    var def = new DbModelDiff();

    var statements = script?.Steps
      .Where(step => step.Change != null && step.Ddl != null)
      .ToLookup(step => step.Change!, step => step.Ddl!);

    foreach (var item in diff.Items) {
      var itemDef = new DbObjectDiff(item.ObjectKind, item.DiffKind,
        GetPath(item.Target ?? item.Source), item.Description, item.IsDestructive);
      if (statements != null)
        itemDef.Statements.AddRange(statements[item]);
      def.Items.Add(itemDef);
    }

    def.Errors.AddRange(diff.Errors);
    return def;
  }

  /// <summary>Gets the qualified path of a db-model object, such as <c>schema.Table.Column</c>.</summary>
  /// <param name="obj">The object, or <see langword="null"/>.</param>
  /// <returns>The path; a schema's and the model's own name; <c>?</c> for <see langword="null"/>.</returns>
  public static string GetPath(DbModelObject? obj) => obj switch {
    null => "?",
    DbColumnInfo column => column.Table == null
      ? column.Name
      : $"{column.Table.Schema.Name}.{column.Table.Name}.{column.Name}",
    DbKeyInfo key => $"{key.Table.Schema.Name}.{key.Table.Name}.{key.Name}",
    DbRefConstraintInfo constraint =>
      $"{constraint.FromTable.Schema.Name}.{constraint.FromTable.Name}.{constraint.Name}",
    DbCheckConstraintInfo check => $"{check.Table.Schema.Name}.{check.Table.Name}.{check.Name}",
    DbTableInfo table => $"{table.Schema.Name}.{table.Name}",
    DbViewInfo view => $"{view.Schema.Name}.{view.Name}",
    DbRoutineInfo routine => $"{routine.Schema.Name}.{routine.Name}",
    DbSequenceInfo sequence => $"{sequence.Schema.Name}.{sequence.Name}",
    _ => obj.Name,      // a schema, and the model itself
  };
}
