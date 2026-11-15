namespace Mic.Linq.Nodes;

/// <summary>Represents a subtree prepared once and placed into many trees: a body, and the placeholder nodes in it,
/// by name, that each instantiation replaces.</summary>
/// <remarks>An extension's filter is one: a condition prepared over a placeholder row, instantiated with the row of
/// each table node a query reads. The body itself is never placed anywhere; <see cref="Instantiate(string, QueryNode)"/>
/// clones it with the placeholders, and every reference to them, replaced, so nothing writes on the body.</remarks>
public sealed class QueryTreeTemplate {
  /// <summary>The name a template over one row gives that row, such as a filter's.</summary>
  public const string RowPlaceholder = "row";

  /// <summary>Gets the prepared subtree.</summary>
  public QueryNode Body { get; }

  /// <summary>Gets the placeholder nodes an instantiation replaces, by the name a replacement is given under.</summary>
  public IReadOnlyDictionary<string, QueryNode> Placeholders { get; }

  /// <summary>Initializes a new instance of the <see cref="QueryTreeTemplate"/> class.</summary>
  /// <param name="body">The prepared subtree.</param>
  /// <param name="placeholders">The placeholder nodes, by name.</param>
  public QueryTreeTemplate(QueryNode body, IReadOnlyDictionary<string, QueryNode> placeholders) {
    ArgumentNullException.ThrowIfNull(body);
    ArgumentNullException.ThrowIfNull(placeholders);
    Body = body;
    Placeholders = placeholders;
  }

  /// <summary>Returns a copy of the body with the one placeholder replaced.</summary>
  /// <param name="name">The name of the placeholder.</param>
  /// <param name="replacement">The node that stands in its place, and in the place of every reference to it.</param>
  /// <returns>The copy's root.</returns>
  public QueryNode Instantiate(string name, QueryNode replacement) =>
    Instantiate(new Dictionary<string, QueryNode> { [name] = replacement });

  /// <summary>Returns a copy of the body with every placeholder replaced.</summary>
  /// <param name="replacements">The node for each placeholder, by the placeholder's name.</param>
  /// <returns>The copy's root.</returns>
  /// <exception cref="ArgumentException">A placeholder is given no replacement, or a replacement names no
  /// placeholder.</exception>
  public QueryNode Instantiate(IReadOnlyDictionary<string, QueryNode> replacements) {
    ArgumentNullException.ThrowIfNull(replacements);
    var byPlaceholder = new Dictionary<QueryNode, QueryNode>(ReferenceEqualityComparer.Instance);
    foreach (var (name, placeholder) in Placeholders) {
      if (!replacements.TryGetValue(name, out var replacement))
        throw new ArgumentException($"The template's placeholder '{name}' is given no replacement.", nameof(replacements));
      byPlaceholder[placeholder] = replacement;
    }
    foreach (var name in replacements.Keys)
      if (!Placeholders.ContainsKey(name))
        throw new ArgumentException($"The template has no placeholder named '{name}'.", nameof(replacements));
    return Body.Clone(node => byPlaceholder.TryGetValue(node, out var replacement) ? replacement : null);
  }
}
