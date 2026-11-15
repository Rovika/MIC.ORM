using Mic.Linq;
using Mic.Linq.ExpressionTree;
using Mic.Orm.Runtime;

namespace Mic.Orm;

/// <summary>The mutations written as queries, called on the session as in Vita: a delete, an update or an insert run
/// now, synchronously or asynchronously, or scheduled into the next save.</summary>
/// <remarks>Each method takes the session's own query and calls the same method on the query:
/// <see cref="MutationQueryableExtensions"/> and <see cref="MutationScheduleExtensions"/>. The query says what the
/// statement does; see those for the shapes a delete, an update and an insert take.</remarks>
public static partial class EntitySessionExtensions {
  // ----- run now ------------------------------------------------------------------------------------------------

  /// <summary>Asynchronously deletes the rows of <typeparamref name="TEntity"/> the query names.</summary>
  /// <typeparam name="TEntity">The entity type whose rows are deleted.</typeparam>
  /// <param name="session">The session the query belongs to.</param>
  /// <param name="query">A query over the entity's own rows, or a query over any entity that projects the entity's
  /// key.</param>
  /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
  /// <returns>A task that represents the asynchronous operation. The task result contains the number of rows
  /// deleted.</returns>
  public static Task<int> ExecuteDeleteAsync<TEntity>(this IEntitySession session, IQueryable query,
      CancellationToken cancellationToken = default) where TEntity : class {
    CheckOwns(session, query);
    return query is IQueryable<TEntity> rows
      ? rows.ExecuteDeleteAsync(cancellationToken)
      : query.ExecuteDeleteAsync<TEntity>(cancellationToken);
  }

  /// <summary>Asynchronously updates the rows of <typeparamref name="TEntity"/> with the values the query projects.
  /// </summary>
  /// <typeparam name="TEntity">The entity type whose rows are updated.</typeparam>
  /// <param name="session">The session the query belongs to.</param>
  /// <param name="query">A query that projects slots named for the entity's members.</param>
  /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
  /// <returns>A task that represents the asynchronous operation. The task result contains the number of rows
  /// updated.</returns>
  public static Task<int> ExecuteUpdateAsync<TEntity>(this IEntitySession session, IQueryable query,
      CancellationToken cancellationToken = default) where TEntity : class {
    CheckOwns(session, query);
    return query.ExecuteUpdateAsync<TEntity>(cancellationToken);
  }

  /// <summary>Asynchronously inserts the rows the query brings into <typeparamref name="TEntity"/>.</summary>
  /// <typeparam name="TEntity">The entity type the rows are inserted into.</typeparam>
  /// <param name="session">The session the query belongs to.</param>
  /// <param name="query">A query that projects slots named for the entity's members.</param>
  /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
  /// <returns>A task that represents the asynchronous operation. The task result contains the number of rows
  /// inserted.</returns>
  public static Task<int> ExecuteInsertAsync<TEntity>(this IEntitySession session, IQueryable query,
      CancellationToken cancellationToken = default) where TEntity : class {
    CheckOwns(session, query);
    return query.ExecuteInsertAsync<TEntity>(cancellationToken);
  }

  /// <summary>Deletes the rows of <typeparamref name="TEntity"/> the query names.</summary>
  /// <typeparam name="TEntity">The entity type whose rows are deleted.</typeparam>
  /// <param name="session">The session the query belongs to.</param>
  /// <param name="query">A query over the entity's own rows, or a query over any entity that projects the entity's
  /// key.</param>
  /// <returns>The number of rows deleted.</returns>
  public static int ExecuteDelete<TEntity>(this IEntitySession session, IQueryable query) where TEntity : class =>
    EntitySessionHelper.GetEntitySession(session).RunSync(() => session.ExecuteDeleteAsync<TEntity>(query));

  /// <summary>Updates the rows of <typeparamref name="TEntity"/> with the values the query projects.</summary>
  /// <typeparam name="TEntity">The entity type whose rows are updated.</typeparam>
  /// <param name="session">The session the query belongs to.</param>
  /// <param name="query">A query that projects slots named for the entity's members.</param>
  /// <returns>The number of rows updated.</returns>
  public static int ExecuteUpdate<TEntity>(this IEntitySession session, IQueryable query) where TEntity : class =>
    EntitySessionHelper.GetEntitySession(session).RunSync(() => session.ExecuteUpdateAsync<TEntity>(query));

  /// <summary>Inserts the rows the query brings into <typeparamref name="TEntity"/>.</summary>
  /// <typeparam name="TEntity">The entity type the rows are inserted into.</typeparam>
  /// <param name="session">The session the query belongs to.</param>
  /// <param name="query">A query that projects slots named for the entity's members.</param>
  /// <returns>The number of rows inserted.</returns>
  public static int ExecuteInsert<TEntity>(this IEntitySession session, IQueryable query) where TEntity : class =>
    EntitySessionHelper.GetEntitySession(session).RunSync(() => session.ExecuteInsertAsync<TEntity>(query));

  // ----- scheduled into the save ----------------------------------------------------------------------------------

  /// <summary>Holds the delete of the rows of <typeparamref name="TEntity"/> the query names for the session's next
  /// save.</summary>
  /// <typeparam name="TEntity">The entity type whose rows are deleted.</typeparam>
  /// <param name="session">The session the query belongs to.</param>
  /// <param name="query">A query over the entity's own rows, or a query over any entity that projects the entity's
  /// key.</param>
  /// <param name="schedule">One of the enumeration values that specifies when within the save's transaction the
  /// statement runs.</param>
  public static void ScheduleDelete<TEntity>(this IEntitySession session, IQueryable query,
      CommandSchedule schedule = CommandSchedule.TransactionEnd) where TEntity : class {
    CheckOwns(session, query);
    if (query is IQueryable<TEntity> rows)
      rows.ScheduleDelete(schedule);
    else
      query.ScheduleDelete<TEntity>(schedule);
  }

  /// <summary>Holds the update of the rows of <typeparamref name="TEntity"/> with the values the query projects for
  /// the session's next save.</summary>
  /// <typeparam name="TEntity">The entity type whose rows are updated.</typeparam>
  /// <param name="session">The session the query belongs to.</param>
  /// <param name="query">A query that projects slots named for the entity's members.</param>
  /// <param name="schedule">One of the enumeration values that specifies when within the save's transaction the
  /// statement runs.</param>
  public static void ScheduleUpdate<TEntity>(this IEntitySession session, IQueryable query,
      CommandSchedule schedule = CommandSchedule.TransactionEnd) where TEntity : class {
    CheckOwns(session, query);
    query.ScheduleUpdate<TEntity>(schedule);
  }

  /// <summary>Holds the insert of the rows the query brings into <typeparamref name="TEntity"/> for the session's
  /// next save.</summary>
  /// <typeparam name="TEntity">The entity type the rows are inserted into.</typeparam>
  /// <param name="session">The session the query belongs to.</param>
  /// <param name="query">A query that projects slots named for the entity's members.</param>
  /// <param name="schedule">One of the enumeration values that specifies when within the save's transaction the
  /// statement runs.</param>
  public static void ScheduleInsert<TEntity>(this IEntitySession session, IQueryable query,
      CommandSchedule schedule = CommandSchedule.TransactionEnd) where TEntity : class {
    CheckOwns(session, query);
    query.ScheduleInsert<TEntity>(schedule);
  }

  /// <summary>Checks that the query was opened on this session, since the statement runs on the query's.</summary>
  private static void CheckOwns(IEntitySession session, IQueryable query) {
    var owner = GetSession(session);
    ArgumentNullException.ThrowIfNull(query);
    if (query.Provider is not EntityQueryProvider { Executor: EntitySession querySession } || querySession != owner)
      throw new ArgumentException("The query is not this session's.", nameof(query));
  }
}
