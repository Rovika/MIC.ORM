using Mic.Common;

namespace Mic.Modeling;

/// <summary>Reads and writes a field on either a generic <see cref="IEntityData"/> record or a CLR object, so that a
/// view binds the same way to both.</summary>
/// <remarks>There is one resolver per view; it holds the view's error sink and the set of messages already reported.
/// A missing field or parameter neither throws nor renders blank: the read returns <see langword="null"/>, the write
/// is a no-op, and the miss is reported through the sink once per distinct message. A found field whose value is
/// <see langword="null"/> is not reported.</remarks>
public sealed class FieldResolver {
  readonly Action<string, Exception?>? _report;
  readonly HashSet<string> _reported = new();   // each distinct message is reported once per view

  /// <summary>Holds the view's named data roots, which a <c>$Name</c> path resolves against absolutely, independent
  /// of the data context; <see langword="null"/> when there are none.</summary>
  readonly IReadOnlyDictionary<string, object?>? _parameters;

  /// <summary>Holds the view's model, used to resolve a bound element's entity name so that a calculated field on
  /// DTO-backed data can be computed; <see langword="null"/> when the view has no model, in which case calculated
  /// fields are not detected on DTOs.</summary>
  readonly DataModel? _model;

  public FieldResolver(Action<string, Exception?>? report, IReadOnlyDictionary<string, object?>? parameters = null, DataModel? model = null) {
    _report = report;
    _parameters = parameters;
    _model = model;
  }

  /// <summary>Represents a resolver with no sink, which never throws and never reports.</summary>
  public static readonly FieldResolver Silent = new(null);

  /// <summary>Determines whether a path references a view parameter, such as <c>$People</c> or
  /// <c>$People.Field</c>.</summary>
  /// <param name="path">The path, or <see langword="null"/>.</param>
  /// <returns><see langword="true"/> if the path references a view parameter; otherwise, <see langword="false"/>.
  /// </returns>
  /// <remarks>Like an <c>@</c> path, a parameter reference is absolute, not relative to the ambient data context.
  /// </remarks>
  public static bool IsParameterRef(string? path) => !string.IsNullOrEmpty(path) && path[0] == '$';

  /// <summary>Gets the value of a single field on a record or object.</summary>
  /// <param name="data">The record or object, or <see langword="null"/>.</param>
  /// <param name="name">The field name.</param>
  /// <param name="context">The id of the element being bound, for the report, or <see langword="null"/>.</param>
  /// <returns>The value, or <see langword="null"/> when <paramref name="data"/> is <see langword="null"/> or the
  /// field does not exist.</returns>
  /// <remarks>A missing field is reported.</remarks>
  public object? GetFieldValue(object? data, string name, string? context) {
    switch (data) {
      case null: return null;
      case IEntityData ed: {
        var field = ed.EntityInfo.FindField(name);
        if (field == null) { ReportMissing(name, ed.EntityInfo.Name, context); return null; }
        if (field is CalculatedField calc)                                    // computed, no stored slot
          return calc.Evaluate(p => Read(ed, p, context, ed.EntityInfo));
        return ed.GetValue(field);
      }
      default: {
        var member = data.GetType().FindField(name);   // property or field, null if absent
        if (member == null) { ReportMissing(name, data.GetType().Name, context); return null; }
        return member.GetFieldValue(data);
      }
    }
  }

  /// <summary>Sets the value of a single field on a record or object.</summary>
  /// <param name="data">The record or object.</param>
  /// <param name="name">The field name.</param>
  /// <param name="value">The value to write.</param>
  /// <param name="context">The id of the element being bound, for the report, or <see langword="null"/>.</param>
  /// <remarks>A missing field is reported and nothing is written. The value is written without coercion; a type
  /// mismatch on a CLR object is caught and reported, and an <see cref="IEntityData"/> store does not throw on one.
  /// </remarks>
  public void SetFieldValue(object data, string name, object? value, string? context) {
    if (data is IEntityData ed) {
      var field = ed.EntityInfo.FindField(name);
      if (field == null) { ReportMissing(name, ed.EntityInfo.Name, context); return; }
      ed.SetValue(field, value);
      return;
    }
    var dm = data.GetType().FindField(name);
    if (dm == null) { ReportMissing(name, data.GetType().Name, context); return; }
    try { dm.SetFieldValue(data, value); }
    catch (Exception ex) {
      Report($"Binding error{Where(context)}: could not write field '{name}' on '{data.GetType().Name}' " +
             $"(value '{value}' of type {value?.GetType().Name ?? "null"}).", ex);
    }
  }

  /// <summary>Gets the value at a dotted path, such as <c>Address.City</c>, drilling each segment.</summary>
  /// <param name="data">The object the path starts from; ignored for a <c>$</c> path.</param>
  /// <param name="dottedPath">The path, or <see langword="null"/>.</param>
  /// <param name="context">The id of the element being bound, for the report, or <see langword="null"/>.</param>
  /// <param name="entityName">The name of the model entity of <paramref name="data"/>, so that a calculated field on
  /// it can be computed, or <see langword="null"/>.</param>
  /// <returns>The value, or <see langword="null"/> when the path is empty, is an <c>@</c> component reference, or
  /// meets a <see langword="null"/> step.</returns>
  /// <remarks>A missing field at any hop is reported; a <see langword="null"/> intermediate value is not. A <c>$</c>
  /// path resolves its head against the view parameters, then walks the remaining segments.</remarks>
  public object? GetValueByPath(object? data, string? dottedPath, string? context, string? entityName = null) =>
    Read(data, dottedPath, context, entityName != null ? _model?.FindEntity(entityName) : null);

  /// <summary>Reads the value at a path, with the model entity of the data already resolved.</summary>
  private object? Read(object? data, string? dottedPath, string? context, EntityInfo? entity) {
    if (string.IsNullOrEmpty(dottedPath) || dottedPath[0] == '@') return null;
    object? start;
    string remaining;
    if (dottedPath[0] == '$') {
      var (name, rest) = SplitHead(dottedPath[1..]);
      if (!TryGetParameter(name, out start, context)) return null;
      remaining = rest;
    }
    else {
      if (data == null) return null;
      start = data;
      remaining = dottedPath;
    }
    return WalkPath(start, remaining, context, entity);
  }

  /// <summary>Drills a dotted remainder from a start object, evaluating a calculated field at a step and advancing
  /// the model entity per hop; an empty remainder yields the start object.</summary>
  private object? WalkPath(object? start, string remaining, string? context, EntityInfo? entity) {
    if (string.IsNullOrEmpty(remaining))
      return start;
    object? current = start;
    EntityInfo? curEntity = entity;
    foreach (var segment in remaining.Split('.')) {
      if (current == null)
        return null;
      var ent = curEntity ?? (current as IEntityData)?.EntityInfo;   // an IEntityData self-describes its entity
      var field = ent?.FindField(segment);
      if (field is CalculatedField calc) {
        var data = current;
        var owner = ent;
        current = calc.Evaluate(p => Read(data, p, context, owner));
        curEntity = null;                                            // a computed value isn't navigated further
        continue;
      }
      current = GetFieldValue(current, segment, context);
      curEntity = NextEntity(field);                                // the model entity for the next hop, if any
    }
    return current;
  }

  /// <summary>Gets the model entity at the end of an entity or entity-list field, or <see langword="null"/> for a
  /// value field.</summary>
  private static EntityInfo? NextEntity(Field? field) =>
    (field?.TypeRef as EntityTypeRef)?.Entity ?? ((field?.TypeRef as ListTypeRef)?.ItemType as EntityTypeRef)?.Entity;

  /// <summary>Splits a path head off its dotted remainder, so that "Person.Address.City" gives "Person" and
  /// "Address.City".</summary>
  private static (string head, string rest) SplitHead(string path) {
    var dot = path.IndexOf('.');
    return dot < 0 ? (path, "") : (path[..dot], path[(dot + 1)..]);
  }

  /// <summary>Looks up a view parameter by name. Returns <see langword="true"/> when the parameter exists, even with
  /// a <see langword="null"/> value; an absent name is reported.</summary>
  private bool TryGetParameter(string name, out object? value, string? context) {
    value = null;
    if (_parameters != null && _parameters.TryGetValue(name, out value)) return true;
    Report($"Binding error{Where(context)}: view parameter '${name}' is not defined.", null);
    return false;
  }

  /// <summary>Gets the value at a dotted path, converted to the specified type.</summary>
  /// <typeparam name="T">The type to convert the value to.</typeparam>
  /// <param name="data">The object the path starts from; ignored for a <c>$</c> path.</param>
  /// <param name="dottedPath">The path, or <see langword="null"/>.</param>
  /// <param name="context">The id of the element being bound, for the report, or <see langword="null"/>.</param>
  /// <param name="entityName">The name of the model entity of <paramref name="data"/>, or <see langword="null"/>.
  /// </param>
  /// <returns>The converted value, or the default of <typeparamref name="T"/> when the value is
  /// <see langword="null"/> or cannot be converted.</returns>
  /// <remarks>A value that cannot be converted is reported; a <see langword="null"/> value is not.</remarks>
  public T? GetValueByPath<T>(object? data, string? dottedPath, string? context, string? entityName = null) {
    var raw = GetValueByPath(data, dottedPath, context, entityName);
    if (raw == null) return default;
    if (ReflectionHelper.TryConvertTo<T>(raw, out var value)) return value;
    Report($"Binding error{Where(context)}: value '{raw}' (type {raw.GetType().Name}) at path '{dottedPath}' " +
           $"can't be shown as {typeof(T).Name}.", null);
    return default;
  }

  /// <summary>Gets the write target of a plain dotted path: the object that owns the final field, and that field's
  /// name.</summary>
  /// <param name="data">The object the path starts from; ignored for a <c>$</c> path.</param>
  /// <param name="path">The path, or <see langword="null"/>.</param>
  /// <param name="context">The id of the element being bound, for the report, or <see langword="null"/>.</param>
  /// <returns>The host and the field name, or <see langword="null"/> for an empty path, an <c>@</c> path, a <c>$</c>
  /// parameter alone, or a <see langword="null"/> intermediate step.</returns>
  /// <remarks>A missing intermediate field is reported during the walk; the final field is checked by
  /// <see cref="SetFieldValue"/> on write.</remarks>
  public (object Host, string Field)? GetHostInstanceAndField(object? data, string? path, string? context) {
    if (string.IsNullOrEmpty(path) || path[0] == '@') return null;
    object? start;
    string remaining;
    if (path[0] == '$') {                          // a $parameter root, then the field path within it
      var (name, rest) = SplitHead(path[1..]);
      if (!TryGetParameter(name, out start, context)) return null;
      remaining = rest;
    }
    else {
      if (data == null) return null;
      start = data;
      remaining = path;
    }
    if (string.IsNullOrEmpty(remaining)) return null;   // "$People" alone has no writable leaf field
    var segments = remaining.Split('.');
    object? host = start;
    for (var i = 0; i < segments.Length - 1; i++) {
      if (host == null) return null;
      host = GetFieldValue(host, segments[i], context);
    }
    return host == null ? null : (host, segments[^1]);
  }

  private void ReportMissing(string field, string typeName, string? context) =>
    Report($"Binding error{Where(context)}: field '{field}' does not exist on '{typeName}' — check the ValuePath for a typo.", null);

  /// <summary>Reports a binding diagnostic through the view's sink, once per distinct message.</summary>
  /// <param name="message">The problem, without the element.</param>
  /// <param name="context">The id of the offending element, or <see langword="null"/>.</param>
  /// <remarks>Component-reference resolution shares this resolver, so a bad reference reports once like a field
  /// typo.</remarks>
  public void ReportError(string message, string? context = null) => Report($"Binding error{Where(context)}: {message}", null);

  private static string Where(string? context) => string.IsNullOrEmpty(context) ? "" : $" on element '{context}'";

  private void Report(string message, Exception? exception) {
    if (_report == null) return;                 // no sink: degrade quietly
    if (!_reported.Add(message)) return;         // once per distinct message per view
    try { _report(message, exception); } catch { /* a reporter must never break resolution */ }
  }
}
