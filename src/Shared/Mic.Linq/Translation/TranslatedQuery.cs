using Mic.Linq.Model;
using Mic.Linq.Nodes;

namespace Mic.Linq.Translation;

/// <summary>Represents what the translator produces: the query's prepared tree, its SQL tree and the description of
/// what the SQL tree returns.</summary>
/// <remarks><para>The prepared tree is what the evaluator consumes; the SQL tree is what the SQL builder and the
/// readers consume. A query that runs in memory has both, since its shape is still read off the SQL tree when a caller
/// skips the entity cache.</para>
/// <para>The translator fills the properties pass by pass, and every one is set when
/// <see cref="QueryTranslator.Translate(QueryInfo, QueryNode, ILinqDataModel, bool)"/> returns.</para></remarks>
public sealed class TranslatedQuery {
  /// <summary>Gets what identifies the query and what it carries: the cache key, the arguments, the options, the
  /// parameters' layout.</summary>
  public QueryInfo Info { get; }

  /// <summary>Gets the original tree after the preparing phase: typed, tiered, preprocessed, still shaped as the
  /// caller wrote it.</summary>
  public QueryNode PreparedRoot { get; internal set; } = null!;

  /// <summary>Gets the includes the query was written with: the references and lists to load with its rows, in the
  /// order they were written.</summary>
  /// <remarks>The includes are stripped off the root before translation; whatever runs the query loads them.</remarks>
  public IReadOnlyList<QueryInclude> Includes { get; internal set; } = [];

  /// <summary>Gets a value that indicates whether the query drops any extension's filters, and so cannot run over the
  /// entity cache.</summary>
  public bool IgnoresFilters { get; internal set; }

  /// <summary>Gets the cached entities anything in the query reads in memory, whether the whole query or a sub-query
  /// inside one that goes to the server.</summary>
  /// <remarks>Whoever runs the query loads these blocks first, for <see cref="ILinqCallContext.GetCachedRows"/>.
  /// </remarks>
  public IReadOnlyList<ILinqEntity> CachedEntities { get; internal set; } = [];

  /// <summary>Gets the cached entities the statement reads in memory, through a sub-query a parameter's getter
  /// evaluates while the statement is bound; empty where the statement reads nothing from memory.</summary>
  /// <remarks>These are distinct from <see cref="CachedEntities"/>, which is what the whole query in memory reads.
  /// </remarks>
  public IReadOnlyList<ILinqEntity> SqlCachedEntities { get; internal set; } = [];

  /// <summary>Gets the highest node of the prepared tree the statement answers; the root itself where nothing runs
  /// after the SQL.</summary>
  /// <remarks>What stands above it runs in C# through <see cref="PostSqlEvaluator"/> over the statement's rows.
  /// </remarks>
  public QueryNode SqlTierTop { get; internal set; } = null!;

  /// <summary>Gets the statement the query is: a select, or one of the mutations.</summary>
  public StatementKind StatementKind { get; internal set; }

  /// <summary>Gets the SQL-shaped tree.</summary>
  public QueryNode SqlRoot { get; internal set; } = null!;

  /// <summary>Gets the common table expressions the statement starts with, its <c>WITH</c> list, in dependency
  /// order.</summary>
  /// <remarks>The list belongs to the statement whatever its root is, so it is beside the root rather than under it.
  /// </remarks>
  public IReadOnlyList<CteNode> Ctes { get; internal set; } = [];

  /// <summary>Gets what the SQL tree's root returns, for the reader.</summary>
  public QueryResultInfo ResultInfo { get; internal set; } = null!;

  /// <summary>Gets the evaluator that runs over what the statement returned, such as a grouping returned as groups
  /// and what was written over the groups; or <see langword="null"/> if the SQL answers the whole query.</summary>
  /// <remarks>The evaluator reads the rows from <see cref="ILinqCallContext.ResultRows"/>, and what it produces is the
  /// query's answer.</remarks>
  public NodeEvaluator? PostSqlEvaluator { get; internal set; }

  /// <summary>Gets the executable for a query that runs in memory, or <see langword="null"/> for one that goes to
  /// the server.</summary>
  public NodeEvaluator? CacheEvaluator { get; internal set; }

  /// <summary>Gets a value that indicates whether the whole query runs in memory, through <see cref="CacheEvaluator"/>.
  /// </summary>
  public bool CanRunInCache => CacheEvaluator != null;

  /// <summary>Initializes a new instance of the <see cref="TranslatedQuery"/> class, for the translator to fill.
  /// </summary>
  /// <param name="info">What identifies the query and what it carries.</param>
  internal TranslatedQuery(QueryInfo info) {
    Info = info;
  }
}
