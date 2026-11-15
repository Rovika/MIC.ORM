using System.Collections;
using System.Linq.Expressions;
using Mic.Common;
using Mic.Linq.Model;
using Mic.Linq.Nodes;
namespace Mic.Linq.ExpressionTree;

/// <summary>Represents the query provider of a typed query, which composes queries as standard LINQ and runs them
/// only asynchronously through its executor.</summary>
/// <remarks>The synchronous <c>Execute</c> methods throw: the client runs on WebAssembly, where blocking on a task
/// deadlocks the one thread there is. A query is run with <c>await q.ToListAsync()</c> or another method of
/// <see cref="AsyncQueryableExtensions"/>.</remarks>
public class EntityQueryProvider : IQueryProvider {
  /// <summary>Initializes a new instance of the <see cref="EntityQueryProvider"/> class.</summary>
  /// <param name="executor">The executor that runs the provider's queries.</param>
  public EntityQueryProvider(IQueryExecutor executor) {
    ArgumentNullException.ThrowIfNull(executor);
    Executor = executor;
  }

  /// <summary>Gets the executor that runs the provider's queries.</summary>
  public IQueryExecutor Executor { get; }

  /// <summary>Creates a query composed over an expression.</summary>
  /// <typeparam name="TElement">The type of the rows.</typeparam>
  /// <param name="expression">The expression.</param>
  /// <returns>A query over the expression.</returns>
  public IQueryable<TElement> CreateQuery<TElement>(Expression expression) => new EntityQuery<TElement>(this, expression);

  /// <summary>Creates a query composed over an expression whose element type is read from the expression.</summary>
  /// <param name="expression">The expression.</param>
  /// <returns>A query over the expression.</returns>
  /// <remarks>LINQ calls this overload only when a query is composed through untyped reflection; the
  /// <c>Queryable</c> extension methods use the generic overload.</remarks>
  public IQueryable CreateQuery(Expression expression) {
    ArgumentNullException.ThrowIfNull(expression);
    var elementType = GetElementType(expression.Type)
      ?? throw new ArgumentException($"'{expression.Type}' is not a queryable sequence.", nameof(expression));
    return (IQueryable)Activator.CreateInstance(typeof(EntityQuery<>).MakeGenericType(elementType), this, expression)!;
  }

  /// <summary>Throws a <see cref="NotSupportedException"/> in all cases, since a query runs only asynchronously.
  /// </summary>
  /// <typeparam name="TResult">The type of the result.</typeparam>
  /// <param name="expression">The expression.</param>
  /// <returns>This method does not return a value.</returns>
  public TResult Execute<TResult>(Expression expression) => throw MakeSyncNotSupported();

  /// <summary>Throws a <see cref="NotSupportedException"/> in all cases, since a query runs only asynchronously.
  /// </summary>
  /// <param name="expression">The expression.</param>
  /// <returns>This method does not return a value.</returns>
  public object Execute(Expression expression) => throw MakeSyncNotSupported();

  internal static NotSupportedException MakeSyncNotSupported() =>
    new("A MIC query runs asynchronously — await ToListAsync(), CountAsync() or one of the other *Async methods. "
      + "There is no synchronous execution: the client runs on WebAssembly, where blocking on a task deadlocks the "
      + "one thread there is.");

  private static Type? GetElementType(Type queryableType) {
    foreach (var candidate in queryableType.GetInterfaces().Append(queryableType))
      if (candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IQueryable<>))
        return candidate.GetGenericArguments()[0];
    return null;
  }
}

/// <summary>Represents a typed query: a whole entity set, or a query composed over one.</summary>
/// <typeparam name="T">The type of the rows.</typeparam>
/// <remarks>Enumerating the query synchronously throws; it is run with <c>await q.ToListAsync()</c> or another method
/// of <see cref="AsyncQueryableExtensions"/>.</remarks>
public class EntityQuery<T> : IOrderedQueryable<T>, IRowSet {
  /// <summary>Initializes a new instance of the <see cref="EntityQuery{T}"/> class as a whole entity set, the root
  /// of a query.</summary>
  /// <param name="provider">The provider.</param>
  /// <param name="entity">The entity whose records the set is all of.</param>
  /// <param name="options">A bitwise combination of the enumeration values that specifies what the caller asks for
  /// beyond the query itself.</param>
  public EntityQuery(EntityQueryProvider provider, ILinqEntity entity, QueryOptions options = QueryOptions.None) {
    ArgumentNullException.ThrowIfNull(provider);
    ArgumentNullException.ThrowIfNull(entity);
    Provider = provider;
    RowEntity = entity;
    Options = options;
    Expression = Expression.Constant(this);
  }

  /// <summary>Initializes a new instance of the <see cref="EntityQuery{T}"/> class as a query composed over an
  /// expression.</summary>
  /// <param name="provider">The provider.</param>
  /// <param name="expression">The expression.</param>
  public EntityQuery(EntityQueryProvider provider, Expression expression) {
    ArgumentNullException.ThrowIfNull(provider);
    ArgumentNullException.ThrowIfNull(expression);
    Provider = provider;
    Expression = expression;
  }

  /// <inheritdoc/>
  public ILinqEntity? RowEntity { get; }

  /// <inheritdoc/>
  public QueryOptions Options { get; }

  /// <inheritdoc/>
  public virtual ulong CacheKey => 0;

  /// <inheritdoc/>
  public virtual QueryNode GetNode() =>
    QueryMaker.From(RowEntity ?? throw new InvalidOperationException("A query composed over a set is not itself a set."));

  /// <summary>Gets the expression of the query.</summary>
  public Expression Expression { get; }

  /// <summary>Gets the type of the rows.</summary>
  public Type ElementType => typeof(T);

  /// <summary>Gets the provider of the query.</summary>
  public EntityQueryProvider Provider { get; }

  IQueryProvider IQueryable.Provider => Provider;

  public IEnumerator<T> GetEnumerator() => throw EntityQueryProvider.MakeSyncNotSupported();

  IEnumerator IEnumerable.GetEnumerator() => throw EntityQueryProvider.MakeSyncNotSupported();

  public override string ToString() => RowEntity != null ? $"{RowEntity.Name} set" : Expression.ToString();
}

/// <summary>Represents the rows a function of the server returns for given arguments, as the root of a typed query.
/// </summary>
/// <typeparam name="T">The type of the rows.</typeparam>
/// <remarks>The arguments are not part of the query's shape: the set is registered as an argument of the run, and
/// each argument is a parameter read off it, so one statement serves every call of the function.</remarks>
public sealed class TableFunctionQuery<T> : EntityQuery<T> {
  /// <summary>Initializes a new instance of the <see cref="TableFunctionQuery{T}"/> class.</summary>
  /// <param name="provider">The provider.</param>
  /// <param name="table">The table of the function's rows, whose name is the function's.</param>
  /// <param name="parameters">The function's parameters, by name and type, in order.</param>
  /// <param name="arguments">The arguments of this call, one per parameter.</param>
  public TableFunctionQuery(EntityQueryProvider provider, ILinqTable table,
      IReadOnlyList<(string Name, Type Type)> parameters, IReadOnlyList<object?> arguments)
      : base(provider, table.Entity ?? throw new ArgumentException("The function's table has no entity.", nameof(table))) {
    ArgumentNullException.ThrowIfNull(parameters);
    ArgumentNullException.ThrowIfNull(arguments);
    if (arguments.Count != parameters.Count)
      throw new ArgumentException(
        $"'{table.Name}' takes {parameters.Count} argument(s) and {arguments.Count} were given.", nameof(arguments));
    Table = table;
    Parameters = parameters;
    Arguments = arguments;
  }

  /// <summary>Gets the table of the function's rows.</summary>
  public ILinqTable Table { get; }

  /// <summary>Gets the function's parameters, by name and type.</summary>
  public IReadOnlyList<(string Name, Type Type)> Parameters { get; }

  /// <summary>Gets the arguments of this call.</summary>
  public IReadOnlyList<object?> Arguments { get; }

  /// <inheritdoc/>
  public override ulong CacheKey =>
    Fnv1aHash.Combine(Fnv1aHash.Combine(CacheKeySeeds.QueryTreeHashSeed, Table.Schema ?? ""), Table.Name + "()");

  /// <summary>Gets the call as a node, each argument a parameter read off this set when the query runs.</summary>
  /// <inheritdoc/>
  public override QueryNode GetNode() {
    var arguments = new QueryNode[Parameters.Count];
    for (var i = 0; i < arguments.Length; i++) {
      var position = i;
      var parameter = QueryMaker.Parameter(Parameters[i].Name, Parameters[i].Type);
      parameter.SetArgValueGetter(this, set => ((TableFunctionQuery<T>)set).Arguments[position]);
      arguments[i] = parameter;
    }
    return QueryMaker.TableFunction(Table, arguments);
  }

  public override string ToString() => $"{Table.Name}({string.Join(", ", Arguments)})";
}
