
namespace Mic.Orm.Db.Model.Upgrades;

/// <summary>Specifies how far an application may go in changing a database.</summary>
/// <remarks>The mode is paired with the <c>DbInstanceType</c> the database declares for itself.</remarks>
public enum DbUpgradeMode {
  /// <summary>No change at all, and no reading: the connect does not load the database or compare it with the
  /// model.</summary>
  Never,
  /// <summary>A change to development and staging databases only.</summary>
  NonProductionOnly,
  /// <summary>A change to whatever database the application is pointed at.</summary>
  Always,
}

/// <summary>Specifies the stages an upgrade runs in, in their fixed order.</summary>
/// <remarks>Renames come first, so every later stage sees an object under the name the model calls it. The
/// destructive stages come last, after every addition and every migration, so a migration can read from a column the
/// same script later drops.</remarks>
public enum DbUpgradeStage {
  /// <summary>The drop of table functions, first of all, since a function may read a view and depends on whatever
  /// the later stages change; a changed function is recreated in <see cref="AddTableFunctions"/>.</summary>
  DropTableFunctions,

  /// <summary>The drop of views, since a view depends on whatever the later stages change; a changed view is
  /// recreated in <see cref="AddViews"/>.</summary>
  DropViews,

  /// <summary>The drop of foreign keys, which reference keys and columns the later stages change.</summary>
  DropRefConstraints,

  /// <summary>The drop of indexes and primary keys, including the ones rebuilt in <see cref="AddKeys"/>.</summary>
  DropKeys,

  /// <summary>The drop of computed columns that are recreated in <see cref="AddColumns"/>, since a computed column
  /// has no alter; after the keys over them, before anything adds.</summary>
  DropComputedColumns,

  RenameTables,
  RenameColumns,

  AddSchemas,
  AddSequences,
  AddTables,

  /// <summary>The addition of new columns, nullable where the model wants them required and nothing supplies a
  /// value. A migration attached here runs after the columns exist and before anything tightens them.</summary>
  AddColumns,

  /// <summary>The backfill of new required columns with a placeholder where they are still null, so they can be made
  /// not null.</summary>
  /// <remarks>This stage follows the migration slot of <see cref="AddColumns"/>, so a migration gets first refusal on
  /// every row and the placeholder fills only what it left behind.</remarks>
  InitColumns,

  /// <summary>Type and default changes, and the closing set-not-null of a new required column.</summary>
  AlterColumns,

  AddKeys,
  AddRefConstraints,

  /// <summary>The creation of views, over tables every earlier stage has finished.</summary>
  AddViews,

  /// <summary>The creation of table functions, over tables and views every earlier stage has finished.</summary>
  AddTableFunctions,

  // ----- destructive, and last for that reason ---------------------------------------------------------------------

  DropColumns,
  DropTables,
  DropSequences,
  DropSchemas,
}
