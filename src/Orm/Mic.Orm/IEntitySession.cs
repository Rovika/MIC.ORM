namespace Mic.Orm;

/// <summary>Defines a virtual connection to the database, through which entities are read and written.</summary>
/// <remarks>Together with the entity declarations, this interface is the surface an application compiles against.
/// Vita's <c>CancelChanges</c> is absent: MIC keeps only current values, so changes cannot be undone without a trip to
/// the database. A member that is declared but not yet built throws when called.</remarks>
public interface IEntitySession {
  /// <summary>Gets the operation this session acts for.</summary>
  OperationContext Context { get; }

  /// <summary>Creates a new entity in memory, which reaches the database when <see cref="SaveChanges"/> is called.
  /// </summary>
  /// <typeparam name="TEntity">The entity interface.</typeparam>
  /// <returns>The new entity.</returns>
  TEntity NewEntity<TEntity>() where TEntity : class;

  /// <summary>Gets the entity with the given primary key.</summary>
  /// <typeparam name="TEntity">The entity interface.</typeparam>
  /// <param name="primaryKeyValue">The primary key value; an array of values for a composite key.</param>
  /// <param name="flags">A bitwise combination of the enumeration values that specifies how to answer when the entity
  /// is not already loaded.</param>
  /// <returns>The entity.</returns>
  TEntity GetEntity<TEntity>(object primaryKeyValue, LoadFlags flags = LoadFlags.Default) where TEntity : class;

  /// <summary>Marks an entity for deletion; the row is deleted when <see cref="SaveChanges"/> is called.</summary>
  /// <typeparam name="TEntity">The entity interface.</typeparam>
  /// <param name="entity">The entity to delete.</param>
  void DeleteEntity<TEntity>(TEntity entity) where TEntity : class;

  /// <summary>Determines whether an entity can be deleted without breaking a referential constraint.</summary>
  /// <typeparam name="TEntity">The entity interface.</typeparam>
  /// <param name="entity">The entity to check.</param>
  /// <param name="blockingEntities">When this method returns, contains the entity types that have rows pointing at
  /// the entity, or an empty array if it can be deleted. A reference marked <c>[CascadeDelete]</c> does not block.
  /// </param>
  /// <returns><see langword="true"/> if the entity can be deleted; otherwise, <see langword="false"/>.</returns>
  bool CanDeleteEntity<TEntity>(TEntity entity, out Type[] blockingEntities) where TEntity : class;

  /// <summary>Checks everything pending and throws if any of it is invalid.</summary>
  void ValidateChanges();

  /// <summary>Writes everything pending to the database, ordered so that referential constraints hold throughout.
  /// </summary>
  /// <param name="options">A bitwise combination of the enumeration values that specifies what the save does beyond
  /// writing.</param>
  void SaveChanges(SaveOptions options = SaveOptions.None);

  /// <summary>Gets the set to write LINQ queries against.</summary>
  /// <typeparam name="TEntity">The entity interface.</typeparam>
  /// <param name="options">A bitwise combination of the enumeration values that specifies what the caller wants
  /// beyond the query itself, such as going to the database where the entity is held whole in memory.</param>
  /// <returns>The queryable entity set.</returns>
  IQueryable<TEntity> EntitySet<TEntity>(Mic.Linq.QueryOptions options = Mic.Linq.QueryOptions.None)
    where TEntity : class;

  Task<TEntity> GetEntityAsync<TEntity>(object primaryKeyValue, LoadFlags flags = LoadFlags.Default)
    where TEntity : class;

  Task SaveChangesAsync(SaveOptions options = SaveOptions.None);

  /// <summary>Asynchronously gets the entity types with rows pointing at an entity, which prevent its deletion.
  /// </summary>
  /// <typeparam name="TEntity">The entity interface.</typeparam>
  /// <param name="entity">The entity to check.</param>
  /// <returns>A task that represents the asynchronous operation. The task result contains the blocking entity types,
  /// or an empty array if the entity can be deleted.</returns>
  Task<Type[]> GetDeleteEntityBlockingTypesAsync<TEntity>(TEntity entity) where TEntity : class;
}
