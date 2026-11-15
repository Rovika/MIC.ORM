using Mic.Linq.Model;
using Mic.Linq.Nodes;

namespace Mic.Linq.ExpressionTree;

/// <summary>Defines the set a typed query is rooted at: a whole entity set, or a query the framework stands in for
/// one.</summary>
/// <remarks>A set reaches the expression tree as a constant expression holding the queryable itself, which is what the
/// scanner and the converter match on. It is the only thing in the tree that names an entity.</remarks>
public interface IRowSet {
  /// <summary>Gets the entity whose records the set is all of, or <see langword="null"/> for a query composed over
  /// one.</summary>
  ILinqEntity? RowEntity { get; }

  /// <summary>Gets the options the caller asked for beyond the query itself.</summary>
  QueryOptions Options { get; }

  /// <summary>Gets the shape of the query the set stands for, or zero for a whole entity set.</summary>
  /// <remarks>The scan hashes the key in place of a tree it never builds; a set with a key is also an argument the
  /// scan registers.</remarks>
  ulong CacheKey { get; }

  /// <summary>Gets the unbound tree the set stands for, as a new node on each call.</summary>
  /// <returns>A node that names the entity's records, or the framework's query.</returns>
  /// <remarks>The tree is transformed in place during translation, so a node lives in one tree only.</remarks>
  QueryNode GetNode();
}
