namespace Mic.Modeling;

/// <summary>Defines a generic in-memory carrier for one entity's data, shaped by the entity's definition.</summary>
/// <remarks>There is no CLR type per entity. A value is read and written by <see cref="Field"/>, whose
/// <see cref="Field.DataIndex"/> indexes the implementation's storage, so a control can cache the field and skip a
/// name lookup. A value field holds its boxed CLR value; an entity field holds a child record; a list field holds a
/// list of them.</remarks>
public interface IEntityData {
  EntityInfo EntityInfo { get; }

  object? GetValue(Field field);
  void SetValue(Field field, object? value);

  /// <summary>Gets the value of a field by name, resolving the field through the entity definition.</summary>
  object? GetValue(string fieldName);
  /// <summary>Sets the value of a field by name, resolving the field through the entity definition.</summary>
  void SetValue(string fieldName, object? value);
}
