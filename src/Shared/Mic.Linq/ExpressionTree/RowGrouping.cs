using System.Collections;
namespace Mic.Linq.ExpressionTree;

/// <summary>Represents one group of a grouping returned as groups: its key and its rows.</summary>
/// <typeparam name="TKey">The type of the key.</typeparam>
/// <typeparam name="TElement">The type of the rows.</typeparam>
public sealed class RowGrouping<TKey, TElement> : IGrouping<TKey, TElement>, IReadOnlyList<TElement> {
  /// <summary>Gets the key the rows of the group share.</summary>
  public TKey Key { get; }

  /// <summary>Gets the number of rows in the group.</summary>
  public int Count => _rows.Count;

  /// <summary>Gets the row at an index.</summary>
  /// <param name="index">The zero-based index of the row.</param>
  public TElement this[int index] => _rows[index];

  readonly IReadOnlyList<TElement> _rows;

  /// <summary>Initializes a new instance of the <see cref="RowGrouping{TKey, TElement}"/> class.</summary>
  /// <param name="key">The key the rows share.</param>
  /// <param name="rows">The rows of the group.</param>
  public RowGrouping(TKey key, IReadOnlyList<TElement> rows) {
    ArgumentNullException.ThrowIfNull(rows);
    Key = key;
    _rows = rows;
  }

  public IEnumerator<TElement> GetEnumerator() => _rows.GetEnumerator();

  IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
