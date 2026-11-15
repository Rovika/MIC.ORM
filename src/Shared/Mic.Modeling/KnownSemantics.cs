namespace Mic.Modeling;

/// <summary>Provides the well-known field semantic values, stored under <see cref="KnownMetaKeys.Semantic"/>: domain
/// refinements of a scalar type that steer the choice of control.</summary>
/// <remarks>An application may define others; these are the ones the built-in control maps recognize.</remarks>
public static class KnownSemantics {
  public const string Email = "Email";
  public const string Password = "Password";
  public const string HtmlColor = "HtmlColor";   // a string holding an HTML/CSS color, such as "#rrggbb"
  public const string MultilineText = "MultilineText";

  /// <summary>Represents a numeric value that is money, formatted as currency with the culture's symbol and decimals
  /// when the view sets no explicit format.</summary>
  public const string Currency = "Currency";

  /// <summary>Represents a string whose value is an entity name, so that a designer can offer a picker over the
  /// model's entities instead of a free-text box.</summary>
  public const string EntityName = "EntityName";
}
