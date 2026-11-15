namespace Mic.Linq.Nodes;

/// <summary>Provides the seed each front end's cache-key space starts from, one per way of building a tree, so that
/// the two spaces never collide.</summary>
/// <remarks>The same logical query need not key alike on the two paths; each hashes whatever is simplest for it.
/// </remarks>
public static class CacheKeySeeds {
  /// <summary>Specifies the seed a tree built by name hashes from.</summary>
  public const ulong QueryTreeHashSeed = 0x9E3779B97F4A7C15;

  /// <summary>Specifies the seed the expression scanner hashes from.</summary>
  public const ulong ExpressionTreeSeed = 0xC2B2AE3D27D4EB4F;
}
