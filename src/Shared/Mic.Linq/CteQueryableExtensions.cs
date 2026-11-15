using System.Linq.Expressions;
using System.Reflection;
using Mic.Linq.Nodes;

namespace Mic.Linq;

/// <summary>Provides extension methods that write a query as a common table expression: a named query the statement
/// defines once in its <c>WITH</c> list, or a recursive one that walks a hierarchy.</summary>
/// <remarks>Each method returns an ordinary query that composes with every other operator. The name, the kind and the
/// depth are part of the statement's shape and must be written as literals, not read from a variable.</remarks>
public static class CteQueryableExtensions {
  static readonly MethodInfo AsCteMethod = Find(nameof(AsCte), 3);
  static readonly MethodInfo AsRecursiveCteMethod = Find(nameof(AsRecursiveCte), 4, genericArguments: 1);
  static readonly MethodInfo AsRecursiveCteWithLevelMethod = Find(nameof(AsRecursiveCte), 5, genericArguments: 2);

  /// <summary>Names a query so that the statement defines it once in its <c>WITH</c> list however often it is read.
  /// </summary>
  /// <typeparam name="T">The type of the rows.</typeparam>
  /// <param name="source">The query to name.</param>
  /// <param name="name">The name of the common table expression. One name is one query within a statement.</param>
  /// <param name="kind">One of the enumeration values that specifies how the common table expression is written.
  /// </param>
  /// <returns>A query whose rows are the rows of <paramref name="source"/>.</returns>
  public static IQueryable<T> AsCte<T>(this IQueryable<T> source, string name, CteKind kind = CteKind.Default) {
    ArgumentNullException.ThrowIfNull(source);
    ArgumentException.ThrowIfNullOrEmpty(name);
    return source.Provider.CreateQuery<T>(Expression.Call(null, AsCteMethod.MakeGenericMethod(typeof(T)),
      source.Expression, Expression.Constant(name), Expression.Constant(kind)));
  }

  /// <summary>Writes a recursive query: the rows of the anchor, and then repeatedly the rows <paramref name="next"/>
  /// reaches from the rows found so far, until it reaches no more.</summary>
  /// <typeparam name="T">The type of the rows.</typeparam>
  /// <param name="anchor">The query whose rows start the recursion.</param>
  /// <param name="name">The name of the common table expression.</param>
  /// <param name="next">A function that, given the rows found so far, returns the rows that come next. For example,
  /// <c>found => found.SelectMany(e => e.Reports)</c> walks down a hierarchy.</param>
  /// <param name="maxDepth">The greatest number of steps from the anchor. It also stops a cycle in the data.</param>
  /// <returns>A query whose rows are the anchor's rows and every row reached from them.</returns>
  public static IQueryable<T> AsRecursiveCte<T>(this IQueryable<T> anchor, string name,
      Expression<Func<IQueryable<T>, IQueryable<T>>> next, int maxDepth = 100) {
    ArgumentNullException.ThrowIfNull(anchor);
    ArgumentException.ThrowIfNullOrEmpty(name);
    ArgumentNullException.ThrowIfNull(next);
    return anchor.Provider.CreateQuery<T>(Expression.Call(null, AsRecursiveCteMethod.MakeGenericMethod(typeof(T)),
      anchor.Expression, Expression.Constant(name), Expression.Quote(next), Expression.Constant(maxDepth)));
  }

  /// <summary>Writes a recursive query that projects each row together with the level it was found at.</summary>
  /// <typeparam name="T">The type of the rows.</typeparam>
  /// <typeparam name="TResult">The type of the projected result.</typeparam>
  /// <param name="anchor">The query whose rows start the recursion.</param>
  /// <param name="name">The name of the common table expression.</param>
  /// <param name="next">A function that, given the rows found so far, returns the rows that come next.</param>
  /// <param name="result">A function that, given a row and the level it was found at, returns the result. The level
  /// is zero for the anchor's rows.</param>
  /// <param name="maxDepth">The greatest number of steps from the anchor. It also stops a cycle in the data.</param>
  /// <returns>A query whose rows are the results <paramref name="result"/> produces.</returns>
  public static IQueryable<TResult> AsRecursiveCte<T, TResult>(this IQueryable<T> anchor, string name,
      Expression<Func<IQueryable<T>, IQueryable<T>>> next, Expression<Func<T, int, TResult>> result,
      int maxDepth = 100) {
    ArgumentNullException.ThrowIfNull(anchor);
    ArgumentException.ThrowIfNullOrEmpty(name);
    ArgumentNullException.ThrowIfNull(next);
    ArgumentNullException.ThrowIfNull(result);
    return anchor.Provider.CreateQuery<TResult>(Expression.Call(null,
      AsRecursiveCteWithLevelMethod.MakeGenericMethod(typeof(T), typeof(TResult)),
      anchor.Expression, Expression.Constant(name), Expression.Quote(next), Expression.Quote(result),
      Expression.Constant(maxDepth)));
  }

  private static MethodInfo Find(string name, int parameters, int genericArguments = 1) =>
    typeof(CteQueryableExtensions).GetMethods(BindingFlags.Public | BindingFlags.Static)
      .Single(m => m.Name == name && m.GetParameters().Length == parameters
        && m.GetGenericArguments().Length == genericArguments);
}
