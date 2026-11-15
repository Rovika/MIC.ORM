namespace Mic.Orm;

/// <summary>Represents a sequence a module registers, as the database keeps it.</summary>
/// <param name="Name">The sequence's name.</param>
/// <param name="Start">The first value.</param>
/// <param name="Increment">The step between values.</param>
/// <param name="DataType">The integer type of the values.</param>
public sealed record SequenceDefinition(string Name, long Start, long Increment, Type DataType);
