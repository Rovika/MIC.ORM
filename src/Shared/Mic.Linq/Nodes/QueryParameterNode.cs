using Mic.Common;
using Mic.Linq.Translation;

namespace Mic.Linq.Nodes;

/// <summary>Represents a value the query is given when it runs, as a leaf carrying its name and type.</summary>
/// <remarks>Anything that varies with data is a parameter and never part of a query's text or its cache key.
/// <see cref="ValueSource"/> says where the value comes from, and whoever runs the query reads it through
/// <see cref="ValueGetter"/> alone, handing every getter the same <see cref="ILinqCallContext"/>. An
/// <see cref="ValueSourceKind.Argument"/> parameter takes a slot among the caller's arguments when the tree's
/// parameters are laid out, which gives it its getter; the argument is the caller's next, or the value the node
/// carries (<see cref="ArgValue"/>). A <see cref="ValueSourceKind.LocalVar"/> parameter is made by translation with
/// its getter, reading through the closure the scan registered at <see cref="ArgIndex"/>. An
/// <see cref="ValueSourceKind.ArgObject"/> parameter is made with the object and a reader
/// (<see cref="SetArgValueGetter"/>), which is its getter, and translation gives it the object's position. A
/// <see cref="ValueSourceKind.Context"/> or <see cref="ValueSourceKind.SubQuery"/> parameter is made with its
/// getter and takes no argument.</remarks>
public sealed class QueryParameterNode : QueryNode {
  /// <summary>Initializes a new instance of the <see cref="QueryParameterNode"/> class.</summary>
  /// <param name="name">The name of the parameter.</param>
  /// <param name="dataType">The type of the value.</param>
  public QueryParameterNode(string name, Type dataType) : base(NodeKind.Parameter) {
    ArgumentException.ThrowIfNullOrEmpty(name);
    ArgumentNullException.ThrowIfNull(dataType);
    Name = name;
    DataType = dataType;
  }

  /// <summary>Gets or sets where the value comes from when the query runs.</summary>
  public ValueSourceKind ValueSource { get; set; }

  /// <summary>Gets or sets the function that reads the value out of the run; <see langword="null"/> until an
  /// argument parameter is laid out.</summary>
  public QueryValueGetter? ValueGetter { get; set; }

  /// <summary>Gets or sets the zero-based position of the argument the value is read off, or -1 for a parameter
  /// that takes no argument.</summary>
  /// <remarks>Several parameters commonly share a position: three reads of one captured closure are one argument.
  /// </remarks>
  public int ArgIndex { get; set; } = -1;

  // ----- Argument -----------------------------------------------------------------------------------------------------

  /// <summary>Gets the argument's value where the node was made with it, boxed since <see langword="null"/> is a
  /// value; or <see langword="null"/> where the caller passes the argument.</summary>
  /// <remarks>The value is not part of the shape: two trees that differ only in what their parameters carry are one
  /// statement.</remarks>
  public ValueBox? ArgValue { get; private set; }

  /// <summary>Gets or sets a value that indicates whether the node was made from a bare value, so that nobody named
  /// it.</summary>
  /// <remarks>Such a node takes a slot of its own and is named for that slot when the tree's parameters are laid out,
  /// which is the same for every tree of the shape.</remarks>
  internal bool IsAutoNamed { get; set; }

  /// <summary>Puts the argument's value on the node, for a parameter made with a value rather than given one.
  /// </summary>
  /// <param name="value">The value, which may be <see langword="null"/>.</param>
  internal void SetArgValue(object? value) => ArgValue = new ValueBox(value);

  /// <summary>Gives an argument parameter its slot among the caller's arguments, and the getter that reads it.
  /// </summary>
  /// <param name="argIndex">The zero-based position of the slot.</param>
  /// <remarks>Called by the layout, before any translation.</remarks>
  internal void AssignSlot(int argIndex) {
    ArgIndex = argIndex;
    ValueGetter = callCtx => callCtx.GetArgument(argIndex);
  }

  // ----- ArgObject ----------------------------------------------------------------------------------------------------

  /// <summary>Gets the object the scan registers as an argument and the value is read off, such as the set an owner
  /// is read off or an array of owners; or <see langword="null"/> once translation has bound the parameter to the
  /// object's position.</summary>
  public object? ArgObject { get; private set; }

  /// <summary>Says that the value is read off an object the query's scan registers as an argument, and makes the
  /// getter that reads it off the argument at <see cref="ArgIndex"/>, which translation fills in.</summary>
  /// <param name="argObject">The object the scan registers.</param>
  /// <param name="read">A function that reads the value off the object. It is kept with the cached statement, so it
  /// must capture nothing of this call.</param>
  public void SetArgValueGetter(object argObject, Func<object, object?> read) {
    ArgumentNullException.ThrowIfNull(argObject);
    ArgumentNullException.ThrowIfNull(read);
    ValueSource = ValueSourceKind.ArgObject;
    ArgObject = argObject;
    ValueGetter = callCtx => callCtx.GetArgument(ArgIndex) is { } argument ? read(argument) : null;
  }

  /// <summary>Binds an argument-object parameter to the object's position among the caller's arguments, and lets
  /// the object go.</summary>
  /// <param name="argIndex">The zero-based position the scan gave the object.</param>
  /// <remarks>Called by translation, once per shape.</remarks>
  internal void BindArg(int argIndex) {
    ArgIndex = argIndex;
    ArgObject = null;
  }

  // ----- SubQuery -----------------------------------------------------------------------------------------------------

  /// <summary>Gets or sets the tree of the sub-query a sub-query parameter runs, or <see langword="null"/> for every
  /// other parameter.</summary>
  /// <remarks>The subtree is kept so that a reader can see what the value is and which cached entities it reads.
  /// </remarks>
  public QueryNode? SubQueryTree { get; set; }
}

/// <summary>Represents a value a <see cref="QueryParameterNode"/> carries, so that <see langword="null"/> is a value
/// and no box is "no value".</summary>
/// <param name="Value">The value, which may be <see langword="null"/>.</param>
public sealed record ValueBox(object? Value);

/// <summary>Specifies where a <see cref="QueryParameterNode"/> takes its value from when the query runs.</summary>
public enum ValueSourceKind {
  /// <summary>The caller's argument at the slot the layout assigns, in the order the tree names its parameters; for a
  /// parameter made with a value, that value. This is the default.</summary>
  Argument = 0,
  /// <summary>A local the typed query captured, read through the closure the scan registered as an argument.
  /// </summary>
  LocalVar,
  /// <summary>An object the scan registers as an argument, read by name, such as the set an owner is read off; bound
  /// to the object's position by translation.</summary>
  ArgObject,
  /// <summary>The run itself, outside the arguments: the operation context, such as the tenant or the current user,
  /// or a read with no object behind it, such as <c>DateTime.Today</c>.</summary>
  Context,
  /// <summary>A sub-query the run evaluates in memory over cached entities.</summary>
  SubQuery,
}
