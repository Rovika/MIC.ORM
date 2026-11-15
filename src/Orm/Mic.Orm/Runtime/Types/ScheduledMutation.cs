using System.Linq.Expressions;
using Mic.Linq;
using Mic.Linq.Nodes;

namespace Mic.Orm.Runtime;

/// <summary>Represents a set-based mutation held for the session's next save: the query that names it, typed or
/// built by name, and when in the save's transaction it runs.</summary>
/// <remarks>Nothing of the query is scanned, converted or translated until the save runs; the executor makes a query
/// request of it then.</remarks>
public sealed class ScheduledMutation {
  /// <summary>Gets the typed mutation, an expression ending in one of the markers of
  /// <see cref="MutationQueryableExtensions"/>; <see langword="null"/> for a mutation built by name.</summary>
  public Expression? Expression { get; }

  /// <summary>Gets the root of a mutation built by name; <see langword="null"/> for a typed one.</summary>
  public QueryNode? Root { get; }

  /// <summary>Gets the values for the parameters a mutation built by name declares.</summary>
  public IReadOnlyList<object?> Arguments { get; }

  /// <summary>Gets when in the save's transaction the mutation runs.</summary>
  public CommandSchedule Schedule { get; }

  /// <summary>Initializes a new instance of the <see cref="ScheduledMutation"/> class for a typed mutation.</summary>
  public ScheduledMutation(Expression mutation, CommandSchedule schedule) {
    ArgumentNullException.ThrowIfNull(mutation);
    Expression = mutation;
    Arguments = [];
    Schedule = schedule;
  }

  /// <summary>Initializes a new instance of the <see cref="ScheduledMutation"/> class for a mutation built by name.
  /// </summary>
  public ScheduledMutation(QueryNode mutation, IReadOnlyList<object?> arguments, CommandSchedule schedule) {
    ArgumentNullException.ThrowIfNull(mutation);
    ArgumentNullException.ThrowIfNull(arguments);
    Root = mutation;
    Arguments = arguments;
    Schedule = schedule;
  }

  public override string ToString() => $"scheduled at {Schedule}";
}
