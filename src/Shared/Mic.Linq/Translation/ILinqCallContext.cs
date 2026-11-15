using Mic.Linq.Model;
namespace Mic.Linq.Translation;

/// <summary>Represents the method that reads a parameter's value when the query runs.</summary>
/// <param name="call">The context the query runs in: the caller's arguments, the operation context, the rows.</param>
/// <returns>The value.</returns>
public delegate object? QueryValueGetter(ILinqCallContext call);

/// <summary>Defines the context one call of a query runs in, handed whole to every value getter and to the evaluator:
/// the caller's arguments, the host's operation context, and how the host's rows are read, made and found.</summary>
/// <remarks>The host implements the context once. A getter is handed everything and reaches for what it needs; one
/// that reaches into the operation context casts it, since the engine does not name its type.</remarks>
public interface ILinqCallContext {
  /// <summary>Gets the caller's arguments: one per parameter in the order the statement writes them for a query
  /// built by name, or the captured objects the scan found for one built from an expression.</summary>
  IReadOnlyList<object?> Arguments { get; }

  /// <summary>Gets the host's operation context, such as which tenant or user is asking, which an extension's filter
  /// reads at run time.</summary>
  object? OperationContext { get; }

  /// <summary>Gets the rows the statement returned, for the evaluator that runs over them; or <see langword="null"/>
  /// where there is no such evaluator.</summary>
  /// <remarks>Whoever ran the statement sets the rows before the evaluator runs.</remarks>
  IEnumerable<object?>? ResultRows { get; }

  /// <summary>Reads a member off a row of the host's, for anything evaluated in memory.</summary>
  /// <param name="row">The row.</param>
  /// <param name="member">The member.</param>
  /// <returns>The value; for a reference member, the row at the other end.</returns>
  object? ReadMember(object row, ILinqEntityMember member);

  /// <summary>Creates a row of a shape the engine synthesised, such as a composite group key or a projection
  /// evaluated in memory.</summary>
  /// <param name="entity">The shape.</param>
  /// <returns>An empty row of the shape.</returns>
  ILinqRow CreateRow(ILinqEntity entity);

  /// <summary>Gets the rows of a cached entity, for a query or a sub-query evaluated in memory.</summary>
  /// <param name="entity">The cached entity.</param>
  /// <returns>The rows, or <see langword="null"/> if the host loaded none for the entity.</returns>
  /// <remarks>The method is synchronous, since a getter runs inside the binding of a statement's parameters; whoever
  /// runs the query loads every entity in <see cref="TranslatedQuery.CachedEntities"/> before it starts.</remarks>
  IReadOnlyList<object>? GetCachedRows(ILinqEntity entity);
}
