using System.Runtime.CompilerServices;
using Mic.Orm.Runtime;

namespace Mic.Orm.Awaiting;

/// <summary>Provides the <see langword="await"/> of an entity and of an entity's list member: the load a property
/// getter would run on the calling thread, run asynchronously.</summary>
/// <remarks>The methods are on every reference type, since an entity is an interface of the application's with no
/// base to constrain them by. They are in a namespace of their own for that reason: a file that imports it can write
/// <c>var owner = await car.Owner;</c>, and only in such a file does an object that is no entity compile under
/// <see langword="await"/>, to be refused when it runs. A task keeps its own awaiter.</remarks>
public static class AwaitExtensions {
  /// <summary>Gets the awaiter of an entity, which loads the entity's row if the entity is a stub.</summary>
  /// <typeparam name="TEntity">The entity interface.</typeparam>
  /// <param name="entity">The entity, such as a reference read off another entity; or <see langword="null"/>.
  /// </param>
  /// <returns>An awaiter whose result is the entity given; <see langword="null"/> for <see langword="null"/>.
  /// </returns>
  /// <remarks>An entity already loaded completes at once and runs nothing. Where the smart load is on, the session's
  /// other stubs of the entity are loaded with it.</remarks>
  /// <exception cref="ArgumentException">The object is not an entity.</exception>
  public static TaskAwaiter<TEntity> GetAwaiter<TEntity>(this TEntity entity) where TEntity : class? =>
    LoadAsync(entity).GetAwaiter();

  /// <summary>Gets the awaiter of an entity's list member, which loads the list's rows if they are not loaded.
  /// </summary>
  /// <typeparam name="TEntity">The entity interface of the list's elements.</typeparam>
  /// <param name="list">The list, read off an entity's list member.</param>
  /// <returns>An awaiter whose result is the list given.</returns>
  /// <remarks>A list already loaded completes at once and runs nothing.</remarks>
  /// <exception cref="ArgumentException">The list is not an entity's list member.</exception>
  public static TaskAwaiter<IList<TEntity>> GetAwaiter<TEntity>(this IList<TEntity> list) where TEntity : class =>
    LoadAsync(list).GetAwaiter();

  private static async Task<TEntity> LoadAsync<TEntity>(TEntity entity) where TEntity : class? {
    if (entity == null)
      return entity;
    var record = EntityHelper.GetRecord(entity);
    if (record.Status == EntityStatus.Stub)
      await GetSession(record).LoadRecordAsync(record);
    return entity;
  }

  private static async Task<IList<TEntity>> LoadAsync<TEntity>(IList<TEntity> list) where TEntity : class {
    ArgumentNullException.ThrowIfNull(list);
    if (list is not IMemberEntityList member)
      throw new ArgumentException(
        $"A '{list.GetType().Name}' is not the list of an entity's list member, which is what await loads.",
        nameof(list));
    if (!member.IsLoaded)
      await GetSession(member.Owner).LoadListAsync(member);
    return list;
  }

  private static EntitySession GetSession(EntityRecord record) =>
    record.Session
      ?? throw new InvalidOperationException(
        $"A '{record.Entity.Name}' with no session has nothing to load through: it was detached from its session.");
}
