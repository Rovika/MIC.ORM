namespace Mic.Modeling;

/// <summary>Provides the group tokens the view builder compacts a field's type into, which a control map keys on,
/// optionally suffixed with "/{Semantic}", to choose the concrete control.</summary>
public static class KnownTypeGroups {
  public const string String = "string";
  public const string Bool = "bool";
  public const string Number = "number";      // all numeric value types collapse here
  public const string Date = "date";          // DateTime and DateOnly
  public const string Enum = "enum";
  public const string FlagsEnum = "flagsEnum";
  public const string List = "list";
  public const string Json = "json";          // a JSON document held whole (JsonTypeRef)
}
