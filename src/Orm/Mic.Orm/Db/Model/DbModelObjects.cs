using Mic.Orm.Model;
namespace Mic.Orm.Db.Model;

/// <summary>Defines the core behavior of a db-model object, which is a name, and provides a base for derived classes.
/// </summary>
public abstract class DbModelObject {
  public string Name { get; }

  protected DbModelObject(string name) { Name = name; }
}

/// <summary>Represents a reference to a database type, as the server declares it.</summary>
public class DbTypeRef {
  /// <summary>Gets the type exactly as the database states it, such as <c>varchar(50)</c>, <c>numeric(18,4)</c> or
  /// <c>geography</c>.</summary>
  public string TypeSpec { get; }

  /// <summary>Gets the CLR type the database type maps to, or <see langword="null"/> for a type read from a database
  /// that no mapping claims.</summary>
  public Type? ClrType { get; }

  public DbTypeRef(string typeSpec, Type? clrType = null) {
    TypeSpec = typeSpec;
    ClrType = clrType;
  }
}

/// <summary>Represents a database schema.</summary>
public class DbSchemaInfo : DbModelObject {
  /// <summary>Gets or sets the entity-model schema this schema came from, or <see langword="null"/> for an
  /// introspected model.</summary>
  public EntitySchemaInfo? EntitySchema { get; set; }

  public DbSchemaInfo(string name) : base(name) { }
}

/// <summary>Represents a sequence, owned by a schema.</summary>
public class DbSequenceInfo : DbModelObject {
  public DbSchemaInfo Schema { get; }
  public Type DataType { get; set; } = typeof(long);

  /// <summary>Gets or sets the value the sequence starts at, or <see langword="null"/> if it is not known.</summary>
  /// <remarks>A sequence read from a server that keeps no record of its start, Oracle, has none; the comparer then
  /// leaves the start out of the comparison. A sequence built from the model always has one.</remarks>
  public long? StartValue { get; set; } = 1;
  public long Increment { get; set; } = 1;

  public DbSequenceInfo(string name, DbSchemaInfo schema) : base(name) {
    Schema = schema;
  }
}
