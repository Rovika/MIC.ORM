namespace Mic.Common;

/// <summary>Computes the 64-bit FNV-1a (Fowler–Noll–Vo) hash.</summary>
/// <remarks>The hash is deterministic across processes and machines, so it suits a value that is persisted or used
/// as a cache key beyond the process; <c>System.HashCode</c> and <c>string.GetHashCode</c> are seeded per process.
/// It is not cryptographic and must not be used where an adversary chooses the input.</remarks>
public static class Fnv1aHash {
  const ulong Offset = 14695981039346656037;
  const ulong Prime = 1099511628211;

  /// <summary>Computes the hash of a span of characters.</summary>
  /// <param name="text">The characters to hash.</param>
  /// <returns>The hash of <paramref name="text"/>.</returns>
  /// <remarks>The UTF-16 code units are hashed, not the UTF-8 bytes; no byte array is allocated.</remarks>
  public static ulong Compute(ReadOnlySpan<char> text) {
    var hash = Offset;
    foreach (var c in text) {
      hash ^= c;
      hash *= Prime;
    }
    return hash;
  }

  public static ulong Compute(string text) => Compute(text.AsSpan());

  /// <summary>Continues a hash over more text, for a key made of several pieces that are not concatenated first.
  /// </summary>
  /// <param name="hash">The hash so far.</param>
  /// <param name="text">The characters to add to the hash.</param>
  /// <returns>The hash over the earlier input and <paramref name="text"/>.</returns>
  public static ulong Combine(ulong hash, ReadOnlySpan<char> text) {
    foreach (var c in text) {
      hash ^= c;
      hash *= Prime;
    }
    return hash;
  }

  /// <summary>Folds one more 64-bit value into a running hash, for a key made of numbers rather than text.</summary>
  /// <param name="hash">The hash so far.</param>
  /// <param name="value">The value to add to the hash.</param>
  /// <returns>The hash over the earlier input and <paramref name="value"/>.</returns>
  public static ulong Combine(ulong hash, ulong value) {
    hash ^= value;
    hash *= Prime;
    return hash;
  }

  /// <summary>Computes the hash of a span of 64-bit values, allocating nothing.</summary>
  /// <param name="values">The values to hash.</param>
  /// <returns>The hash of <paramref name="values"/>.</returns>
  public static ulong Compute(ReadOnlySpan<ulong> values) {
    var hash = Offset;
    foreach (var value in values)
      hash = Combine(hash, value);
    return hash;
  }

  /// <summary>Formats a hash as fixed-width lower-case hexadecimal, so the text sorts and compares as the number does.
  /// </summary>
  /// <param name="hash">The hash to format.</param>
  /// <returns>The 16-character hexadecimal form of <paramref name="hash"/>.</returns>
  public static string ToHex(ulong hash) => hash.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);

  public static string ComputeHex(string text) => ToHex(Compute(text));
}
