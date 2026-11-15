namespace Mic.Linq;

/// <summary>Provides methods that read a JSON document inside a query, which the translator writes as the server's
/// own JSON function.</summary>
/// <remarks>These methods throw when called outside a query. A property declared on a typed JSON member, such as
/// <c>p.Spec.Weight</c>, is read by path without them; they serve a document with no declared type, such as a string
/// member holding JSON, or a path the declared type does not name.</remarks>
public static class JsonFunctions {
  /// <summary>Reads the value at a path in a JSON document.</summary>
  /// <typeparam name="T">The type the value is read as.</typeparam>
  /// <param name="document">The JSON document, or a member holding one.</param>
  /// <param name="path">The path of the value, such as <c>$.a.b</c>.</param>
  /// <returns>The value at <paramref name="path"/>, or <see langword="null"/> if the path names nothing.</returns>
  public static T? Value<T>(object? document, string path) =>
    throw new InvalidOperationException(
      $"{nameof(JsonFunctions)}.{nameof(Value)} is written inside a query, where the server reads the document; it "
      + "does not run in memory.");
}
