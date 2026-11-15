namespace Mic.Common;

/// <summary>Represents a key/value lookup for a small number of entries, as a linear scan over one array in place of
/// a dictionary.</summary>
/// <remarks>Below about ten entries a scan beats a hash and allocates nothing beyond the array; use it where the
/// count is known to be small, never as a general map. Keys compare with <see cref="EqualityComparer{T}.Default"/>.
/// The indexer's setter replaces an existing key, as a dictionary's does; <see cref="Add"/> appends without looking
/// and is for a caller who knows the key is new.</remarks>
/// <typeparam name="TKey">The type of a key.</typeparam>
/// <typeparam name="TValue">The type of a value.</typeparam>
public sealed class SmallLookup<TKey, TValue> where TKey : notnull {
  (TKey Key, TValue Value)[] _entries = new (TKey, TValue)[8];
  int _count;
  static readonly EqualityComparer<TKey> Comparer = EqualityComparer<TKey>.Default;

  public int Count => _count;

  public TValue this[TKey key] {
    get => IndexOf(key) is var i and >= 0 ? _entries[i].Value : throw new KeyNotFoundException($"Key '{key}' was not found.");
    set {
      var i = IndexOf(key);
      if (i >= 0)
        _entries[i].Value = value;
      else
        Add(key, value);
    }
  }

  public void Add(TKey key, TValue value) {
    if (_count == _entries.Length)
      Array.Resize(ref _entries, _count * 2);
    _entries[_count++] = (key, value);
  }

  public bool TryGetValue(TKey key, out TValue value) {
    var i = IndexOf(key);
    if (i < 0) {
      value = default!;
      return false;
    }
    value = _entries[i].Value;
    return true;
  }

  public bool ContainsKey(TKey key) => IndexOf(key) >= 0;

  /// <summary>Removes the entry with the specified key, keeping the rest in order.</summary>
  /// <param name="key">The key of the entry to remove.</param>
  /// <returns><see langword="true"/> if the entry was found and removed; otherwise, <see langword="false"/>.</returns>
  public bool Remove(TKey key) {
    var i = IndexOf(key);
    if (i < 0)
      return false;
    _count--;
    if (i < _count)
      Array.Copy(_entries, i + 1, _entries, i, _count - i);
    _entries[_count] = default;
    return true;
  }

  /// <summary>Finds the position of the entry with the specified key by a linear scan.</summary>
  /// <param name="key">The key to find.</param>
  /// <returns>The zero-based index of the entry, or -1 when the key is not present.</returns>
  public int IndexOf(TKey key) {
    for (var i = 0; i < _count; i++)
      if (Comparer.Equals(_entries[i].Key, key))
        return i;
    return -1;
  }

  public IEnumerable<TKey> Keys {
    get { for (var i = 0; i < _count; i++) yield return _entries[i].Key; }
  }

  public IEnumerable<TValue> Values {
    get { for (var i = 0; i < _count; i++) yield return _entries[i].Value; }
  }

  public void Clear() {
    Array.Clear(_entries, 0, _count);
    _count = 0;
  }
}

/// <summary>Represents a set for a small number of members, as a linear scan over one array. This class cannot be
/// inherited.</summary>
/// <typeparam name="T">The type of a member.</typeparam>
public sealed class SmallSet<T> where T : notnull {
  T[] _items = new T[8];
  int _count;
  static readonly EqualityComparer<T> Comparer = EqualityComparer<T>.Default;

  public int Count => _count;

  /// <summary>Adds an item unless it is already present.</summary>
  /// <param name="item">The item to add.</param>
  /// <returns><see langword="true"/> if the item was added; <see langword="false"/> if it was already present.
  /// </returns>
  public bool Add(T item) {
    if (Contains(item))
      return false;
    if (_count == _items.Length)
      Array.Resize(ref _items, _count * 2);
    _items[_count++] = item;
    return true;
  }

  public bool Contains(T item) {
    for (var i = 0; i < _count; i++)
      if (Comparer.Equals(_items[i], item))
        return true;
    return false;
  }

  public bool Remove(T item) {
    for (var i = 0; i < _count; i++)
      if (Comparer.Equals(_items[i], item)) {
        _count--;
        if (i < _count)
          Array.Copy(_items, i + 1, _items, i, _count - i);
        _items[_count] = default!;
        return true;
      }
    return false;
  }
}
