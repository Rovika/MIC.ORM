using System.Linq.Expressions;
using Mic.Linq;
using Mic.Linq.ExpressionTree;
using Mic.Orm.Runtime;

namespace Mic.Orm;

/// <summary>Provides extension methods that hold a mutation written as a query for the session's next save, to run
/// within the save's transaction at the start or the end as its <see cref="CommandSchedule"/> says.</summary>
/// <remarks><para>Each method is the scheduled form of an <c>Execute…Async</c> method of
/// <see cref="MutationQueryableExtensions"/> and takes the same query. A scheduled statement is pending work like a
/// modified record: the save runs it and a failed save keeps it.</para>
/// <para>The query's captured locals are read when the statement is scheduled: a local assigned again before the
/// save, or a loop variable, binds the value it had at the call. What a local refers to is not copied. A captured
/// entity is the same entity at the save, so a key the save generates for it is the key bound, and a member of
/// <see langword="this"/> is read at the save.</para></remarks>
public static class MutationScheduleExtensions {
  /// <summary>Holds the delete of the rows the query finds for the session's next save.</summary>
  /// <typeparam name="T">The entity type of the rows.</typeparam>
  /// <param name="source">The query that names the rows to delete.</param>
  /// <param name="schedule">One of the enumeration values that specifies when within the save's transaction the
  /// statement runs.</param>
  public static void ScheduleDelete<T>(this IQueryable<T> source, CommandSchedule schedule = CommandSchedule.TransactionEnd) =>
    GetSession(source).Schedule(MutationQueryableExtensions.MakeDelete(source), schedule);

  /// <summary>Holds the delete of the rows of <typeparamref name="TTarget"/> whose key the query brings for the
  /// session's next save.</summary>
  /// <typeparam name="TTarget">The entity type whose rows are deleted.</typeparam>
  /// <param name="source">A query over any entity that projects the target's key.</param>
  /// <param name="schedule">One of the enumeration values that specifies when within the save's transaction the
  /// statement runs.</param>
  public static void ScheduleDelete<TTarget>(this IQueryable source, CommandSchedule schedule = CommandSchedule.TransactionEnd)
      where TTarget : class =>
    GetSession(source).Schedule(MutationQueryableExtensions.MakeDeleteByKey<TTarget>(source), schedule);

  /// <summary>Holds the update of the rows of <typeparamref name="TTarget"/> for the session's next save.</summary>
  /// <typeparam name="TTarget">The entity type whose rows are updated.</typeparam>
  /// <param name="source">A query that projects slots named for the target's members.</param>
  /// <param name="schedule">One of the enumeration values that specifies when within the save's transaction the
  /// statement runs. At the end, the update runs over what the save has just written.</param>
  public static void ScheduleUpdate<TTarget>(this IQueryable source, CommandSchedule schedule = CommandSchedule.TransactionEnd)
      where TTarget : class =>
    GetSession(source).Schedule(MutationQueryableExtensions.MakeUpdate<TTarget>(source), schedule);

  /// <summary>Holds the insert of the rows the query brings into <typeparamref name="TTarget"/> for the session's
  /// next save.</summary>
  /// <typeparam name="TTarget">The entity type the rows are inserted into.</typeparam>
  /// <param name="source">A query that projects slots named for the target's members.</param>
  /// <param name="schedule">One of the enumeration values that specifies when within the save's transaction the
  /// statement runs.</param>
  public static void ScheduleInsert<TTarget>(this IQueryable source, CommandSchedule schedule = CommandSchedule.TransactionEnd)
      where TTarget : class =>
    GetSession(source).Schedule(MutationQueryableExtensions.MakeInsert<TTarget>(source), schedule);

  private static EntitySession GetSession(IQueryable source) {
    ArgumentNullException.ThrowIfNull(source);
    return source.Provider is EntityQueryProvider { Executor: EntitySession session }
      ? session
      : throw new ArgumentException("The query is not a session's; it has no save to schedule a mutation into.", nameof(source));
  }
}
