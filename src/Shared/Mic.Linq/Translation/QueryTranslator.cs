using Mic.Linq.ExpressionTree;
using Mic.Linq.Model;
using Mic.Linq.Nodes;

namespace Mic.Linq.Translation;

/// <summary>Translates a query tree, as either front end builds it, into a prepared tree, a SQL-shaped tree and the
/// description its readers need.</summary>
/// <remarks><para>The translation runs in phases, each a pass over the tree. The first phase prepares the original
/// tree in place: it binds the tree to the model, takes the values the caller brought out as parameters,
/// converts C#'s operations into the engine's and entities the caller wrote into keys, marks what runs in memory
/// over cached entities, and marks what SQL cannot do and has to run over what comes back. The prepared tree is what
/// the evaluator consumes.</para>
/// <para>The second phase transforms the prepared tree into a tree structured like SQL: selects with clauses, tables,
/// joins and columns, with output lists built on demand. The third phase builds the shape of the result, what the
/// root returns, finalising its output list. The SQL builder, which walks the SQL tree, is the ORM's.</para></remarks>
public sealed partial class QueryTranslator {
  readonly QueryInfo _info;
  readonly ILinqDataModel _model;
  // Every entity is read from the server, a cached one too: the caller skipped the entity cache, or holds a lock.
  readonly bool _readsServerOnly;
  // Binds the root; a table's filter is bound once, when the model prepares it.
  readonly QueryModelBinder _modelBinder;
  // The tree as the passes leave it: every first-phase pass replaces the root it was given.
  QueryNode _root;
  // The product, filled pass by pass.
  readonly TranslatedQuery _translatedQuery;
  // How many parameters the cache tier made for sub-queries evaluated in memory.
  int _cachedParameterCount;

  /// <summary>Initializes a new instance of the <see cref="QueryTranslator"/> class.</summary>
  /// <param name="info">What identifies the query and what it carries.</param>
  /// <param name="root">The root of the query's tree, with its parameters laid out.</param>
  /// <param name="model">The host's model.</param>
  /// <param name="skipEntityCache"><see langword="true"/> to leave every sub-query over a cached entity in the
  /// statement; otherwise, <see langword="false"/>.</param>
  private QueryTranslator(QueryInfo info, QueryNode root, ILinqDataModel model, bool skipEntityCache) {
    _info = info;
    _root = root;
    _model = model;
    // A lock is on the server's rows, and it is part of the query's key; the skip is not, so its caller says it.
    _readsServerOnly = skipEntityCache
      || (info.Options & (QueryOptions.LockForUpdate | QueryOptions.SkipLocked)) != 0;
    _modelBinder = new QueryModelBinder(model);
    _translatedQuery = new TranslatedQuery(info);
  }

  /// <summary>Translates a query: binds its tree to the model, prepares it, transforms it into the SQL tree, and builds
  /// the shape of its result.</summary>
  /// <param name="info">What identifies the query and what it carries.</param>
  /// <param name="root">The root of the query's tree, with its parameters laid out.</param>
  /// <param name="model">The host's model, with its mapping to storage, which entities it holds in memory and the
  /// filters its extensions attach.</param>
  /// <param name="skipEntityCache"><see langword="true"/> to read every entity from the server, so that no
  /// sub-query over a cached entity is answered from memory; otherwise, <see langword="false"/>.</param>
  /// <returns>The translated query.</returns>
  /// <remarks>The statement is built even for a query that runs in memory, since the shape still needs it when a
  /// caller asks for <see cref="QueryOptions.SkipEntityCache"/>. That statement may still take a sub-query over a
  /// cached entity as a parameter answered from memory; <paramref name="skipEntityCache"/> gives the one that does
  /// not. A query that locks its rows is always translated so.</remarks>
  public static TranslatedQuery Translate(QueryInfo info, QueryNode root, ILinqDataModel model,
      bool skipEntityCache = false) {
    ArgumentNullException.ThrowIfNull(info);
    ArgumentNullException.ThrowIfNull(root);
    ArgumentNullException.ThrowIfNull(model);
    return new QueryTranslator(info, root, model, skipEntityCache).Translate();
  }

  /// <summary>Runs the passes over the tree and assembles the translated query.</summary>
  private TranslatedQuery Translate() {
    // The includes come off first. An include node changes neither the statement nor the rows, only what is loaded with
    // them afterwards; it is a node so that the scanner hashes it into the key, and once the key is taken the list of
    // includes is all the result needs. Left in, every pass below would have to learn to see through it: the binder,
    // the three tier markings, the evaluator and the conversions.
    StripIncludes();
    BindArgObjects();
    _modelBinder.Bind(_root);
    AssignPreSql();
    PreprocessOps();
    var canRunAllInCache = MarkRunnableInCache();
    AssignPostSql();
    _translatedQuery.SqlCachedEntities = CollectSqlCachedEntities();
    var sqlTierTop = FindSqlTierTop();
    _translatedQuery.SqlTierTop = sqlTierTop;
    _translatedQuery.StatementKind = TranslationHelper.GetStatementKind(sqlTierTop);
    // The SQL tree is built from a copy: the transform writes onto what it walks, and the prepared tree stays as the
    // evaluators read it.
    var sqlRoot = TransformToSqlTree(sqlTierTop.Clone());
    // A lock is the root select's: only a select over a table can hold the rows it finds.
    var lockOptions = _info.Options & (QueryOptions.LockForUpdate | QueryOptions.SkipLocked);
    if (lockOptions != 0) {
      if (sqlRoot is not SelectNode locked)
        throw new NotSupportedException("A query locking its rows for update is a select — not a set operation, a condition or a mutation.");
      locked.LocksForUpdate = true;
      locked.SkipsLocked = lockOptions.HasFlag(QueryOptions.SkipLocked);
    }
    _translatedQuery.SqlRoot = sqlRoot;
    _translatedQuery.Ctes = _cteList;
    _translatedQuery.ResultInfo = BuildResultInfo(sqlRoot);
    _translatedQuery.PostSqlEvaluator = ReferenceEquals(sqlTierTop, _root) ? null : new NodeEvaluatorBuilder(sqlTierTop).Build(_root);
    _translatedQuery.CacheEvaluator = canRunAllInCache ? new NodeEvaluatorBuilder().Build(_root) : null;
    return _translatedQuery;
  }

  /// <summary>Translates a condition written over the row of a table node, on its own: binds it to the model,
  /// converts its operations and entity comparisons, and transforms it into columns of that row.</summary>
  /// <param name="condition">The condition, written over a table node's row.</param>
  /// <param name="model">The host's model, with its mapping to storage.</param>
  /// <returns>The condition in SQL-tree form, over the same row.</returns>
  /// <remarks>This is how a host prepares a fragment it will place into many queries, such as a table's
  /// <see cref="ILinqTable.Filters"/>. A fragment has no identity and takes no arguments, so it is translated under an
  /// empty query info.</remarks>
  public static QueryNode TranslateCondition(QueryNode condition, ILinqDataModel model) {
    ArgumentNullException.ThrowIfNull(condition);
    ArgumentNullException.ThrowIfNull(model);
    var translator = new QueryTranslator(new QueryInfo(0, [], QueryOptions.None), condition, model,
      skipEntityCache: false);
    translator._modelBinder.Bind(condition);
    return PreprocessOps(condition).Transform(translator.TransformValue, bottomUp: true);
  }

  /// <summary>Takes the include nodes off the root, keeping them for the result, and gives the result its prepared
  /// root: the same tree, which the passes after prepare in place.</summary>
  private void StripIncludes() {
    _root = TranslationHelper.StripWrappers(_root, out var includes, out _ignoresAllFilters, out _ignoredFilterOwners);
    _translatedQuery.PreparedRoot = _root;
    _translatedQuery.Includes = includes;
    _translatedQuery.IgnoresFilters = _ignoresAllFilters || _ignoredFilterOwners.Count > 0;
  }

  bool _ignoresAllFilters;
  IReadOnlyList<Type> _ignoredFilterOwners = [];

  /// <summary>Determines whether the query drops the filters of the extension that attached one.</summary>
  private bool IsFilterIgnored(Type? owner) =>
    _ignoresAllFilters || (owner != null && _ignoredFilterOwners.Any(ignored => ignored.IsAssignableFrom(owner)));

  /// <summary>Binds each <see cref="ValueSourceKind.ArgObject"/> parameter to its object's position in the scan,
  /// which gives it its argument index and its getter.</summary>
  /// <remarks>This runs first, since the entity conversions derive key parameters from it and need its position.
  /// </remarks>
  private void BindArgObjects() =>
    _root.ForEach(node => {
      if (node is not QueryParameterNode { ArgObject: { } argObject } parameter)
        return;
      var index = _info.IsTyped ? _info.IndexOf(argObject) : -1;
      if (index < 0)
        throw new InvalidOperationException(
          $"Parameter '{parameter.Name}' reads its value off an object the query's scan did not register.");
      parameter.BindArg(index);
      parameter.Tier = QueryTier.PreSql;
    });

  /// <summary>Finds the highest node on the Sql tier, which the SQL is built from; whatever is PostSql above it runs
  /// in C# over what the statement returns.</summary>
  private QueryNode FindSqlTierTop() {
    var node = _root;
    while (node.Tier == QueryTier.PostSql && node is MethodCallNode call)
      node = call.Argument(0);
    return node;
  }
}
