using Mic.Linq.Model;
using Mic.Linq.Nodes;

namespace Mic.Linq.Translation;

/// <summary>Represents what a translated query returns, as the reader needs it: the form, the entity or shape a row
/// is, the members the columns come back as in order, the type of a value, and for a projection that hands whole rows
/// on, where each row's columns lie.</summary>
/// <remarks>The reader takes column 0 as the first member, so the members are carried from the translation rather
/// than derived again.</remarks>
public sealed class QueryResultInfo {
  /// <summary>Initializes a new instance of the <see cref="QueryResultInfo"/> class.</summary>
  /// <param name="kind">One of the enumeration values that specifies what the query returns.</param>
  /// <param name="entity">The row shape, or <see langword="null"/> for a query that returns values.</param>
  /// <param name="members">The members the columns come back as, in order.</param>
  /// <param name="scalarType">The type of a returned value, or <see langword="null"/> for a query that returns rows.
  /// </param>
  /// <param name="slots">Where each slot's columns lie, for a bundle; or <see langword="null"/>.</param>
  public QueryResultInfo(QueryResultKind kind, ILinqEntity? entity, IReadOnlyList<ILinqEntityMember> members,
      Type? scalarType = null, IReadOnlyList<QueryResultSlot>? slots = null) {
    Kind = kind;
    Entity = entity;
    Members = members;
    ScalarType = scalarType;
    Slots = slots;
  }

  /// <summary>Gets what the query returns, which chooses the reader.</summary>
  public QueryResultKind Kind { get; }

  /// <summary>Gets the row shape: the model's entity for <see cref="QueryResultKind.Entities"/>, the structure the
  /// engine synthesized for <see cref="QueryResultKind.Rows"/>, the entity the statement wrote for
  /// <see cref="QueryResultKind.Affected"/>; or <see langword="null"/> for a query that returns values.</summary>
  public ILinqEntity? Entity { get; }

  /// <summary>Gets the members the columns come back as, in order; for a bundle, every slot's members one after
  /// another.</summary>
  public IReadOnlyList<ILinqEntityMember> Members { get; }

  /// <summary>Gets the type the caller asked for of the one value an aggregate returns, or of the column a collapsed
  /// projection returns; or <see langword="null"/> for a query that returns rows.</summary>
  public Type? ScalarType { get; }

  /// <summary>Gets where each slot's columns lie, for a bundle of whole rows; or <see langword="null"/> for any other
  /// result.</summary>
  public IReadOnlyList<QueryResultSlot>? Slots { get; }

  /// <inheritdoc/>
  public override string ToString() => Kind switch {
    QueryResultKind.Entities => $"Entities of {Entity?.Name}, {Members.Count} columns",
    QueryResultKind.Rows => $"Rows of {Entity?.Name}, {Members.Count} columns",
    QueryResultKind.Values => $"Values of {ScalarType?.Name}",
    QueryResultKind.Affected => "Rows affected",
    _ => $"Scalar {ScalarType?.Name}",
  };
}

/// <summary>Represents one slot of a bundle's row: a whole row of an entity read from <see cref="Count"/> columns
/// starting at <see cref="Offset"/>, or one value read from the column at <see cref="Offset"/>. This class cannot be
/// inherited.</summary>
/// <param name="Member">The slot's member on the bundle shape, whose field the row stores the value under.</param>
/// <param name="Entity">The entity a row slot holds, or <see langword="null"/> for a value slot.</param>
/// <param name="Members">The members a row slot's columns come back as, in order; empty for a value slot.</param>
/// <param name="Offset">The zero-based index of the slot's first column in the row.</param>
/// <param name="Count">The number of columns the slot takes: the members' count, or one for a value.</param>
/// <param name="ValueType">The type a value slot reads as, or <see langword="null"/> for a row slot.</param>
public sealed record QueryResultSlot(ILinqEntityMember Member, ILinqEntity? Entity, IReadOnlyList<ILinqEntityMember> Members,
  int Offset, int Count, Type? ValueType) {
  /// <summary>Gets a value that indicates whether the slot holds a whole row.</summary>
  public bool IsRow => Entity != null;
}
