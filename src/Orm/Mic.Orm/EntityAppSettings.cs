using Mic.Orm.Logging;

namespace Mic.Orm;

/// <summary>Holds the settings that control how an application's model is built.</summary>
/// <remarks>The settings are read during <see cref="EntityApp.Init"/> and not afterwards; changing one once the model
/// exists has no effect.</remarks>
public class EntityAppSettings {
  /// <summary>Specifies the schema a module's entities go in when the module names no schema of its own.</summary>
  /// <remarks><see langword="null"/> means the provider's own default schema.</remarks>
  public string? DefaultSchema;

  /// <summary>Specifies how entity and database names are derived.</summary>
  public NamingPolicy NamingPolicy = new();

  /// <summary>Specifies the size of a string member that declares none.</summary>
  /// <remarks>Exceeding the size is an error, not a truncation; <c>[Size(n, SizeOptions.AutoTrim)]</c> asks for
  /// trimming.</remarks>
  public int DefaultStringSize = 50;

  public byte DefaultDecimalPrecision = 18;
  public byte DefaultDecimalScale = 4;

  /// <summary>Specifies whether the module that holds MIC's own bookkeeping entities is added to the application
  /// automatically.</summary>
  /// <remarks>On by default. Off for an application that never lets MIC manage its schema.</remarks>
  public bool IncludeDbInfoModule = true;

  /// <summary>Specifies the layout of the Guids the ORM mints.</summary>
  /// <remarks>The default, <see cref="GuidScheme.Auto"/>, takes each database's provider's scheme; another value
  /// applies to every database of the application.</remarks>
  public GuidScheme GuidScheme = GuidScheme.Auto;

  /// <summary>Specifies the log mode every new <see cref="OperationContext"/> starts with.</summary>
  /// <remarks>A context's own <see cref="OperationContext.LogMode"/> can be changed afterwards. The default is
  /// <see cref="LogMode.None"/>.</remarks>
  public LogMode LogMode = LogMode.None;

  /// <summary>Specifies how many entries one operation accumulates before the cap applies.</summary>
  /// <remarks>At the cap, <see cref="LogMode.Details"/> writes a batch and <see cref="LogMode.OnError"/> drops the
  /// oldest entry.</remarks>
  public int LogBufferSize = 1000;
}
