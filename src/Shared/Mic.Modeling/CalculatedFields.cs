using System.Globalization;
using System.Text;

namespace Mic.Modeling;

/// <summary>Represents a field that is contributed by a <see cref="DataModelExtension"/> rather than stored, and is
/// computed at read time; provides a base for the calculated field kinds.</summary>
/// <remarks>A calculated field is nullable and read-only; its value is never written back.</remarks>
public abstract class CalculatedField : Field {
  /// <summary>Called from constructors in derived classes to initialize the CalculatedField class.</summary>
  /// <param name="name">The field name.</param>
  /// <param name="kindFlag">The flag that marks the kind of calculated field.</param>
  /// <param name="deleted"><see langword="true"/> to mark the field as deleted; otherwise, <see langword="false"/>.
  /// </param>
  /// <remarks>The type reference is initialized to string; <see cref="Resolve"/> may replace it.</remarks>
  protected CalculatedField(string name, FieldFlags kindFlag, bool deleted)
    : base(name, new ScalarTypeRef(typeof(string)),
           kindFlag | FieldFlags.Nullable | (deleted ? FieldFlags.Deleted : FieldFlags.None)) { }

  /// <summary>When overridden in a derived class, resolves the result type reference and builds the cached state
  /// against the owning entity and model.</summary>
  /// <param name="owner">The entity that owns the field.</param>
  /// <param name="model">The model the entity belongs to.</param>
  /// <remarks>The extension calls this after all of its calculated fields are present, so a template can reference
  /// another calculated field.</remarks>
  public abstract void Resolve(EntityInfo owner, DataModel model);

  /// <summary>When overridden in a derived class, evaluates the value for a data instance.</summary>
  /// <param name="read">A resolver that returns the value of a possibly dotted path on the instance.</param>
  /// <returns>The computed value, or <see langword="null"/>.</returns>
  /// <remarks>The binding layer supplies the resolver; a nested calculated field resolves through it recursively.
  /// </remarks>
  public abstract object? Evaluate(Func<string, object?> read);
}

/// <summary>Represents a calculated field whose value is text with <c>{path}</c> tokens, such as
/// <c>{City}, {State}</c>.</summary>
/// <remarks>A template that is exactly one bare token, such as <c>{Zip}</c> or <c>{Address.City}</c>, is a
/// pass-through: the field's type is the target path's type and <see cref="Evaluate"/> returns that value unchanged.
/// Any other template composes a culture-aware string. A token may carry a .NET format specification, such as
/// <c>{Price:C}</c>, which forces the string form even for a lone token.</remarks>
public sealed class TemplatedField : CalculatedField {
  /// <summary>Gets the template text.</summary>
  public string Template { get; }

  string _format = "";                              // composite format, e.g. "{0}, {1:C}"
  string[] _paths = System.Array.Empty<string>();   // one value path per placeholder, in order
  bool _passthrough;                                // the template is exactly one bare token

  /// <summary>Initializes a new instance of the TemplatedField class.</summary>
  /// <param name="name">The field name.</param>
  /// <param name="template">The template text.</param>
  /// <param name="deleted"><see langword="true"/> to mark the field as deleted; otherwise, <see langword="false"/>.
  /// </param>
  public TemplatedField(string name, string template, bool deleted = false)
    : base(name, FieldFlags.Templated, deleted) { Template = template; }

  public override void Resolve(EntityInfo owner, DataModel model) {
    (_format, _paths) = CalcTemplate.Parse(Template);
    _passthrough = _format == "{0}" && _paths.Length == 1;   // a lone "{path}" with no literal text and no format spec
    if (_passthrough) {
      var type = WalkPathType(owner, _paths[0]);
      if (type != null)
        TypeRef = type;   // a broken path keeps the string placeholder and reads as null
    }
  }

  public override object? Evaluate(Func<string, object?> read) {
    if (_passthrough)
      return read(_paths[0]);   // the raw typed value, not a formatted string
    var args = new object?[_paths.Length];
    for (var i = 0; i < _paths.Length; i++)
      args[i] = read(_paths[i]);
    return string.Format(CultureInfo.CurrentCulture, _format, args);
  }

  /// <summary>Gets the type reference of the terminal field of a dotted path over the model, following entity and
  /// entity-list fields at each segment. Returns <see langword="null"/> for a broken path.</summary>
  private static TypeRef? WalkPathType(EntityInfo owner, string path) {
    EntityInfo? entity = owner;
    Field? field = null;
    foreach (var seg in path.Split('.')) {
      field = entity?.FindField(seg);
      if (field == null)
        return null;
      entity = (field.TypeRef as EntityTypeRef)?.Entity
            ?? ((field.TypeRef as ListTypeRef)?.ItemType as EntityTypeRef)?.Entity;
    }
    return field?.TypeRef;
  }
}

/// <summary>Parses a value template such as <c>{City}, {State:C}</c> into a .NET composite format string such as
/// <c>{0}, {1:C}</c> and the ordered token paths.</summary>
/// <remarks>Literal braces are written as <c>{{</c> and <c>}}</c>.</remarks>
static class CalcTemplate {
  public static (string format, string[] paths) Parse(string template) {
    var fmt = new StringBuilder(template.Length + 8);
    var paths = new List<string>();
    int i = 0, n = template.Length;
    while (i < n) {
      var ch = template[i];
      if (ch == '{') {
        if (i + 1 < n && template[i + 1] == '{') { fmt.Append("{{"); i += 2; continue; }   // literal {
        var close = template.IndexOf('}', i + 1);
        if (close < 0) { fmt.Append(template.Substring(i).Replace("{", "{{").Replace("}", "}}")); break; }   // unclosed
        var token = template.Substring(i + 1, close - i - 1);
        var colon = token.IndexOf(':');                       // split path[:format]
        var path = (colon < 0 ? token : token[..colon]).Trim();
        var spec = colon < 0 ? null : token[(colon + 1)..];
        fmt.Append('{').Append(paths.Count);
        if (spec != null)
          fmt.Append(':').Append(spec);
        fmt.Append('}');
        paths.Add(path);
        i = close + 1;
      }
      else if (ch == '}') {
        if (i + 1 < n && template[i + 1] == '}') { fmt.Append("}}"); i += 2; continue; }   // literal }
        fmt.Append("}}"); i++;                                                             // lone } -> literal
      }
      else { fmt.Append(ch); i++; }
    }
    return (fmt.ToString(), paths.ToArray());
  }
}
