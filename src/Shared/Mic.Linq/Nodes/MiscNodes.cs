using Mic.Common;

namespace Mic.Linq.Nodes;

/// <summary>Represents a literal, which is part of the query's shape and is written into its text and its cache key,
/// as a leaf carrying the value and its type.</summary>
/// <remarks>A collection carries the element type and <see cref="NodeFlags.List"/>. <see langword="null"/> is a
/// value. The value is folded into the cache key, so a value that varies must be a parameter instead.</remarks>
public sealed class ConstantNode : QueryNode {
  /// <summary>Initializes a new instance of the <see cref="ConstantNode"/> class.</summary>
  /// <param name="value">The value.</param>
  /// <param name="dataType">The type of the value, or <see langword="null"/> if not known.</param>
  public ConstantNode(object? value, Type? dataType) : base(NodeKind.Constant) {
    Value = value;
    DataType = dataType;
  }

  /// <summary>Gets the value.</summary>
  public object? Value { get; }

  /// <inheritdoc/>
  protected internal override ulong AddCustomNodeHash(ulong hash) =>
    Value == null ? hash : Fnv1aHash.Combine(hash, Value.ToString() ?? "");

  /// <inheritdoc/>
  public override string ToString() => Value?.ToString() ?? "null";
}

/// <summary>Represents a named operation over its arguments, such as <c>Upper</c>, <c>Like</c> or <c>Year</c>, whose
/// text the dialect owns. The children are the arguments.</summary>
/// <remarks>A <see cref="BinaryNode"/> is an operator every dialect writes identically; a named operation is one whose
/// text varies by vendor. See <see cref="NamedOpType"/> for the operations.</remarks>
public sealed class NamedOpNode : QueryNode {
  /// <summary>Initializes a new instance of the <see cref="NamedOpNode"/> class.</summary>
  /// <param name="opType">One of the enumeration values that specifies the operation.</param>
  /// <param name="dataType">The type of the value the operation produces.</param>
  /// <param name="arguments">An array that contains the arguments.</param>
  public NamedOpNode(NamedOpType opType, Type dataType, params QueryNode[] arguments)
      : base(NodeKind.NamedOp, arguments) {
    ArgumentNullException.ThrowIfNull(dataType);
    if (opType == NamedOpType.None)
      throw new ArgumentException("A named operation has to say which one it is.", nameof(opType));
    OpType = opType;
    DataType = dataType;
  }

  /// <summary>Gets which operation this is.</summary>
  public NamedOpType OpType { get; }

  /// <summary>Folds the operation into the cache key, since <c>UPPER(x)</c> and <c>LOWER(x)</c> are one kind and two
  /// statements.</summary>
  /// <param name="hash">The hash so far.</param>
  /// <returns>The hash with the operation folded in.</returns>
  protected internal override ulong AddCustomNodeHash(ulong hash) => Fnv1aHash.Combine(hash, (ulong)OpType);

  /// <inheritdoc/>
  public override string ToString() => $"{OpType}({ChildCount})";
}

/// <summary>Represents <c>And</c>, <c>Or</c> or <c>Not</c> over its children, as <see cref="OpType"/> says, producing
/// a Boolean.</summary>
/// <remarks><c>And</c> and <c>Or</c> take any number of operands and are flattened as they are built, so a chain of
/// them is one node; <c>Not</c> has one operand.</remarks>
public sealed class LogicalOpNode : QueryNode {
  /// <summary>Initializes a new instance of the <see cref="LogicalOpNode"/> class.</summary>
  /// <param name="opType">One of the enumeration values that specifies the operator.</param>
  /// <param name="operands">An array that contains the operands.</param>
  public LogicalOpNode(LogicalOpType opType, params QueryNode[] operands) : base(NodeKind.LogicalOp, operands) {
    if (opType == LogicalOpType.None)
      throw new ArgumentException("A logical operation has to say which one it is.", nameof(opType));
    OpType = opType;
    DataType = typeof(bool);
  }

  /// <summary>Gets which operator this is.</summary>
  public LogicalOpType OpType { get; }

  /// <summary>Folds the operator into the cache key, since <c>a AND b</c> and <c>a OR b</c> are two statements.
  /// </summary>
  /// <param name="hash">The hash so far.</param>
  /// <returns>The hash with the operator folded in.</returns>
  protected internal override ulong AddCustomNodeHash(ulong hash) => Fnv1aHash.Combine(hash, (ulong)OpType);

  /// <inheritdoc/>
  public override string ToString() => $"{OpType}({ChildCount})";
}

/// <summary>Represents a query together with the members to load with its rows; the first child is the query.</summary>
/// <remarks>An include changes neither the statement nor its reader, but it is hashed with the shape, so a query with
/// and without one are two cache entries. The translator strips the node off the root with
/// <see cref="Translation.TranslationHelper.StripIncludes"/> and records the includes on what it produces.</remarks>
public sealed class IncludeNode : QueryNode {
  /// <summary>Gets the includes, in the order they were written.</summary>
  public IReadOnlyList<QueryInclude> Includes { get; }

  /// <summary>Initializes a new instance of the <see cref="IncludeNode"/> class over a query.</summary>
  /// <param name="source">The query the rows come from.</param>
  /// <param name="includes">The includes, at least one.</param>
  public IncludeNode(QueryNode source, IReadOnlyList<QueryInclude> includes) : this(includes) {
    ArgumentNullException.ThrowIfNull(source);
    AddChild(source);
  }

  /// <summary>Initializes a new instance of the <see cref="IncludeNode"/> class whose source the walk that makes it
  /// adds as its child.</summary>
  /// <param name="includes">The includes, at least one.</param>
  public IncludeNode(IReadOnlyList<QueryInclude> includes) : base(NodeKind.Include) {
    ArgumentNullException.ThrowIfNull(includes);
    if (includes.Count == 0)
      throw new ArgumentException("An include names at least one member.", nameof(includes));
    Includes = includes;
  }

  /// <summary>Gets the query the rows come from.</summary>
  public QueryNode Source => Children[0];

  /// <inheritdoc/>
  protected internal override ulong AddCustomNodeHash(ulong hash) {
    foreach (var include in Includes)
      hash = include.AddHash(hash);
    return hash;
  }

  /// <inheritdoc/>
  public override string ToString() => $"include {string.Join(", ", Includes)}";
}

/// <summary>Represents one include a query carries: the members to load with the rows, as a selector over an entity,
/// which applies wherever that entity's rows are loaded by the query, or as a member path of the query's own result
/// entity by name, such as <c>Category.Products</c>, which applies to its rows alone.</summary>
public sealed class QueryInclude {
  /// <summary>Gets the selector of a typed include, or <see langword="null"/> for one by name.</summary>
  public System.Linq.Expressions.LambdaExpression? Selector { get; }

  /// <summary>Gets the dotted member path of an include by name, or <see langword="null"/> for a typed one.</summary>
  public string? Path { get; }

  /// <summary>Gets the entity type the selector is written over, or <see langword="null"/> for an include by name,
  /// which applies to the query's result entity.</summary>
  public Type? EntityType => Selector?.Parameters[0].Type;

  /// <summary>Initializes a new instance of the <see cref="QueryInclude"/> class from a selector.</summary>
  /// <param name="selector">The selector, taking one parameter, the entity.</param>
  public QueryInclude(System.Linq.Expressions.LambdaExpression selector) {
    ArgumentNullException.ThrowIfNull(selector);
    if (selector.Parameters.Count != 1)
      throw new ArgumentException("An include selector takes one parameter, the entity.", nameof(selector));
    Selector = selector;
  }

  /// <summary>Initializes a new instance of the <see cref="QueryInclude"/> class from a member path.</summary>
  /// <param name="path">The dotted member path.</param>
  public QueryInclude(string path) {
    ArgumentException.ThrowIfNullOrWhiteSpace(path);
    Path = path;
  }

  /// <summary>Folds the include into a shape's cache key: the entity it is written over and what it names.</summary>
  /// <param name="hash">The hash so far.</param>
  /// <returns>The hash with the include folded in.</returns>
  public ulong AddHash(ulong hash) {
    hash = Fnv1aHash.Combine(hash, EntityType?.FullName ?? "");
    return Fnv1aHash.Combine(hash, ToString());
  }

  /// <inheritdoc/>
  public override string ToString() => Path ?? Selector!.ToString();
}


/// <summary>Represents a query read without the filters its entities' extensions attach: all of them, or one
/// extension's; the first child is the query.</summary>
/// <remarks>The host wraps a query in it: a view's select, which is fixed text, or every query of a session that has
/// an extension disabled. The translator strips the node off the root with
/// <see cref="Translation.TranslationHelper.StripWrappers"/> and leaves the named filters out of every table it
/// reads. A query without filters never runs over the entity cache, whose blocks were filled under them.</remarks>
public sealed class IgnoreFiltersNode : QueryNode {
  /// <summary>Gets the extension whose filters are dropped.</summary>
  /// <value>The extension type, or <see langword="null"/> for every extension's filters.</value>
  public Type? ExtensionType { get; }

  /// <summary>Initializes a new instance of the <see cref="IgnoreFiltersNode"/> class over a query.</summary>
  /// <param name="source">The query.</param>
  /// <param name="extensionType">The extension whose filters are dropped, or <see langword="null"/> for all.</param>
  public IgnoreFiltersNode(QueryNode source, Type? extensionType) : this(extensionType) {
    ArgumentNullException.ThrowIfNull(source);
    AddChild(source);
  }

  /// <summary>Initializes a new instance of the <see cref="IgnoreFiltersNode"/> class whose source the walk that
  /// makes it adds as its child.</summary>
  /// <param name="extensionType">The extension whose filters are dropped, or <see langword="null"/> for all.</param>
  public IgnoreFiltersNode(Type? extensionType) : base(NodeKind.IgnoreFilters) => ExtensionType = extensionType;

  /// <summary>Gets the query.</summary>
  public QueryNode Source => Children[0];

  /// <inheritdoc/>
  protected internal override ulong AddCustomNodeHash(ulong hash) =>
    Fnv1aHash.Combine(hash, ExtensionType?.FullName ?? "*");

  /// <inheritdoc/>
  public override string ToString() => ExtensionType == null ? "ignore filters" : $"ignore filters of {ExtensionType.Name}";
}
