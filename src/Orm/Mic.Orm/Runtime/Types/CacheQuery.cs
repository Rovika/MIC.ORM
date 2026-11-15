using Mic.Linq.Model;
using Mic.Linq.Translation;
namespace Mic.Orm.Runtime;

/// <summary>Represents a query the entity cache answers: the evaluator the translator built over its tree, the
/// cached entities it reads, and whether it asked for one row.</summary>
public sealed class CacheQuery(NodeEvaluator evaluator, IReadOnlyList<ILinqEntity> entities, bool returnsSingleRow) {
  /// <summary>Gets the evaluator that runs the query over the cached rows.</summary>
  public NodeEvaluator Evaluator { get; } = evaluator;

  /// <summary>Gets the cached entities the evaluator reads, whose blocks are loaded before it runs.</summary>
  public IReadOnlyList<ILinqEntity> Entities { get; } = entities;

  /// <summary>Gets a value that indicates whether the query asked for one row and answers a list of at most one.
  /// </summary>
  public bool ReturnsSingleRow { get; } = returnsSingleRow;
}
