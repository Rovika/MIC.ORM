namespace Mic.Linq.Translation;

/// <summary>Provides what the engine asks of a call context beyond its members.</summary>
public static class LinqCallContextHelper {
  /// <summary>Gets the argument at a position.</summary>
  /// <param name="call">The call context.</param>
  /// <param name="index">The zero-based index of the argument.</param>
  /// <returns>The argument, or <see langword="null"/> if there is none at that position.</returns>
  /// <remarks>A missing argument is tolerated, since the evaluator is handed a condition's own argument list, which
  /// may be shorter than the statement's.</remarks>
  public static object? GetArgument(this ILinqCallContext call, int index) =>
    index >= 0 && index < call.Arguments.Count ? call.Arguments[index] : null;
}
