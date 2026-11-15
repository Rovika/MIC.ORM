using Mic.Common;
using Mic.Linq.Model;

namespace Mic.Linq.Nodes;

/// <summary>Defines the core behavior of a node that produces rows, such as an entity set, a queryable call over one
/// or two of them combined, and provides a base for derived classes.</summary>
/// <remarks>A row of the rowset is a <see cref="RowNode"/> that points at its rowset rather than being a child of it;
/// a member access holds no entity of its own, since what it is read from is its first child.</remarks>
public abstract class RowSetNode : QueryNode {
  /// <summary>Called from constructors in derived classes to initialize the <see cref="RowSetNode"/> class.</summary>
  /// <param name="kind">One of the enumeration values that specifies what the node is.</param>
  /// <param name="entity">The entity the node produces rows of, or <see langword="null"/> if not yet decided.</param>
  /// <param name="rowsOf">The rowset whose rows <see cref="Row"/> is a row of, which is the source for a queryable
  /// call; or <see langword="null"/> for the node's own rows.</param>
  /// <param name="children">An array that contains the children, in order.</param>
  protected RowSetNode(NodeKind kind, ILinqEntity? entity, RowSetNode? rowsOf, params QueryNode[] children)
      : base(kind, children) {
    Entity = entity;
    Row = new RowNode(rowsOf ?? this);
  }

  /// <summary>Gets or sets the entity the node produces rows of: a table's entity, or the structure a projection
  /// defined.</summary>
  /// <remarks>A projection decides its shape after its slots are built, and a join decides which side it returns
  /// after its result selector is seen.</remarks>
  public ILinqEntity? Entity { get; set; }

  /// <summary>Gets or sets what the rowset is called in the SQL, such as <c>t0</c>, once the SQL tree has been
  /// built; or <see langword="null"/> before.</summary>
  public string? Alias { get; set; }

  /// <summary>Gets or sets the select the rowset is a source of in the SQL tree, or <see langword="null"/> for the
  /// outermost select and before the SQL tree exists.</summary>
  /// <remarks>A column reference asks the owner select to learn whether it is in scope, an outer row, or a value to
  /// project out of a derived table.</remarks>
  public SelectNode? OwnerSelect { get; set; }

  /// <summary>Gets the row handed to the lambdas written over the rowset, one object however many times it is
  /// mentioned.</summary>
  /// <remarks>For a queryable call the row is a row of the source, since <c>Where(e => …)</c> writes <c>e</c> over
  /// the rows coming in; for a leaf it is a row of the set itself. The row is not a child; every member access written
  /// over it references it.</remarks>
  public RowNode Row { get; private set; }

  /// <inheritdoc/>
  protected internal override QueryNode CopyNode() {
    var copy = (RowSetNode)base.CopyNode();
    copy.Row = new RowNode(copy);
    return copy;
  }

  /// <inheritdoc/>
  protected internal override void RelinkClone(QueryTreeCloner cloner) => OwnerSelect = cloner.MapOrNull(OwnerSelect);
}

/// <summary>Represents all the records of one entity, as a leaf.</summary>
public sealed class EntitySetNode : RowSetNode {
  /// <summary>Initializes a new instance of the <see cref="EntitySetNode"/> class.</summary>
  /// <param name="entity">The entity.</param>
  public EntitySetNode(ILinqEntity entity) : base(NodeKind.EntitySet, entity, null) =>
    ArgumentNullException.ThrowIfNull(entity);

  /// <summary>Gets the entity.</summary>
  public new ILinqEntity Entity => base.Entity!;
}

/// <summary>Represents the rows of a JSON array: a source whose rows are the elements of one document, either a
/// parameter's value bound as one JSON text or a document read off the outer row, typed by the columns declared. The
/// first child is the document.</summary>
/// <remarks>A parameter's rows are one bound value however many rows it holds, joined on every column like any other
/// source; a column's rows are correlated to the outer row. The entity the rows are rows of is made by the model at
/// binding from <see cref="Specs"/> or the element type; the cache key is hashed from the specs.</remarks>
public sealed class JsonRowsNode : RowSetNode {
  /// <summary>Gets the document the rows come out of: a parameter, or a value read off the outer row.</summary>
  public QueryNode Document => Children[0];

  /// <summary>Gets the parameter the rows come out of, or <see langword="null"/> if they come out of the outer row.
  /// </summary>
  public QueryParameterNode? Parameter => Document as QueryParameterNode;

  /// <summary>Gets a value that indicates whether the rows come out of the outer row, so that the source has to stand
  /// beside it and read it.</summary>
  public bool IsCorrelated => Parameter == null;

  /// <summary>Gets what each column is made from, in column order.</summary>
  public IReadOnlyList<LinqMemberSpec> Specs { get; }

  /// <summary>Gets the element type the rows are typed after, or <see langword="null"/> if the columns were declared.
  /// </summary>
  public Type? ElementType { get; }

  /// <summary>Gets the members the rows' columns are typed after, in column order: the model members the node was
  /// made over, or the members of its entity once it has one.</summary>
  public IReadOnlyList<ILinqEntityMember> Columns => _columns ?? Entity.Members;

  /// <summary>Gets or initializes a value that indicates whether the elements are scalars, so that the rows have the
  /// one column <see cref="Translation.JsonTranslationHelper.JsonElementColumn"/> and a lambda's parameter over them
  /// stands for the value rather than for a row.</summary>
  public bool IsScalar { get; init; }

  /// <summary>Gets a value that indicates whether binding has given the node the entity its rows are rows of.
  /// </summary>
  public bool HasEntity => base.Entity != null;

  /// <summary>Gets the name the node's entity is made under.</summary>
  public string EntityName => $"rows of {Parameter?.Name ?? Document.Kind.ToString()}";

  /// <summary>Gets the entity the rows are rows of.</summary>
  /// <exception cref="InvalidOperationException">The tree has not been bound.</exception>
  public new ILinqEntity Entity => base.Entity
    ?? throw new InvalidOperationException($"The {EntityName} have no entity yet — the tree has not been bound.");

  readonly IReadOnlyList<ILinqEntityMember>? _columns;

  /// <summary>Initializes a new instance of the <see cref="JsonRowsNode"/> class with columns typed after model
  /// members.</summary>
  /// <param name="document">The document the rows come out of.</param>
  /// <param name="columns">The model members the columns are typed after, in column order.</param>
  public JsonRowsNode(QueryNode document, IReadOnlyList<ILinqEntityMember> columns)
      : base(NodeKind.JsonRows, null, null, document) {
    ArgumentNullException.ThrowIfNull(columns);
    var specs = new LinqMemberSpec[columns.Count];
    for (var i = 0; i < specs.Length; i++)
      specs[i] = new LinqMemberSpec(columns[i].Name, columns[i].DataType
        ?? throw new NotSupportedException($"'{columns[i].Name}' is no value member, so it cannot type a column of the rows."),
        columns[i].IsNullable);
    Specs = CheckSpecs(specs);
    _columns = columns;
  }

  /// <summary>Initializes a new instance of the <see cref="JsonRowsNode"/> class with columns declared by name and
  /// type.</summary>
  /// <param name="document">The document the rows come out of.</param>
  /// <param name="specs">The columns, in order.</param>
  public JsonRowsNode(QueryNode document, IReadOnlyList<LinqMemberSpec> specs)
      : base(NodeKind.JsonRows, null, null, document) {
    Specs = CheckSpecs(specs);
  }

  /// <summary>Initializes a new instance of the <see cref="JsonRowsNode"/> class for the rows of a JSON array of an
  /// element type, whose entity is made at binding.</summary>
  /// <param name="document">The document the rows come out of.</param>
  /// <param name="elementType">The type of the array's elements.</param>
  public JsonRowsNode(QueryNode document, Type elementType) : base(NodeKind.JsonRows, null, null, document) {
    ArgumentNullException.ThrowIfNull(elementType);
    ElementType = elementType;
    Specs = Translation.JsonTranslationHelper.GetJsonColumnSpecs(elementType);
  }

  /// <summary>Initializes a new instance of the <see cref="JsonRowsNode"/> class for the rows of a JSON array of an
  /// element type, over the entity already made for it.</summary>
  /// <param name="document">The document the rows come out of.</param>
  /// <param name="elementType">The type of the array's elements.</param>
  /// <param name="entity">The entity already made for the element type.</param>
  public JsonRowsNode(QueryNode document, Type elementType, ILinqEntity entity) : this(document, elementType) {
    ArgumentNullException.ThrowIfNull(entity);
    base.Entity = entity;
  }

  /// <inheritdoc/>
  protected internal override ulong AddCustomNodeHash(ulong hash) {
    foreach (var spec in Specs)
      hash = Fnv1aHash.Combine(Fnv1aHash.Combine(hash, spec.Name), spec.DataType.FullName ?? "");
    return Fnv1aHash.Combine(hash, IsScalar ? "scalar" : "");
  }

  /// <inheritdoc/>
  public override string ToString() {
    var source = Parameter != null ? "rows of " + Parameter.Name : $"rows of {Document}";
    return Alias == null ? source : $"{source} {Alias}";
  }

  /// <summary>Gives the node the entity its rows are rows of, once, at binding.</summary>
  internal void SetEntity(ILinqEntity entity) {
    ArgumentNullException.ThrowIfNull(entity);
    base.Entity = entity;
  }

  private static IReadOnlyList<LinqMemberSpec> CheckSpecs(IReadOnlyList<LinqMemberSpec> specs) {
    ArgumentNullException.ThrowIfNull(specs);
    if (specs.Count == 0)
      throw new ArgumentException("The rows of a document need at least one column.", nameof(specs));
    return specs;
  }
}


/// <summary>Represents two whole rowsets combined by <see cref="OpType"/>: the first child is the left side, the
/// second the right, and the shape is the left side's.</summary>
public sealed class SetOperationNode : RowSetNode {
  /// <summary>Initializes a new instance of the <see cref="SetOperationNode"/> class.</summary>
  /// <param name="opType">One of the enumeration values that specifies the combination.</param>
  /// <param name="left">The left side.</param>
  /// <param name="right">The right side.</param>
  /// <param name="entity">The entity the node produces rows of, or <see langword="null"/> if not yet decided.</param>
  public SetOperationNode(SetOperationType opType, QueryNode left, QueryNode right, ILinqEntity? entity)
      : base(NodeKind.SetOperation, entity, null, left, right) {
    if (opType == SetOperationType.None)
      throw new ArgumentException("A set operation has to say which one it is.", nameof(opType));
    OpType = opType;
  }

  /// <summary>Gets which combination this is.</summary>
  public SetOperationType OpType { get; }
  /// <summary>Gets the left side.</summary>
  public QueryNode Left => Children[0];
  /// <summary>Gets the right side.</summary>
  public QueryNode Right => Children[1];

  /// <summary>Folds the operation into the cache key, since <c>UNION</c> and <c>UNION ALL</c> over the same two sides
  /// are two statements.</summary>
  /// <param name="hash">The hash so far.</param>
  /// <returns>The hash with the operation folded in.</returns>
  protected internal override ulong AddCustomNodeHash(ulong hash) => Fnv1aHash.Combine(hash, (ulong)OpType);

  /// <inheritdoc/>
  public override string ToString() => OpType.ToString();
}

/// <summary>Represents one row of a rowset: what a lambda's parameter becomes, and what every member access written
/// over it is read from.</summary>
/// <remarks>The row is a leaf that points at its rowset and is not a child of it. Every mention of the lambda's
/// parameter is this one object, so a tree is a directed acyclic graph at exactly this kind; a pass that mutates a
/// row node does so once per reference. A list member under an aggregate is wrapped as a rowset first, so a row is
/// always a row of a rowset.</remarks>
public sealed class RowNode : QueryNode {
  /// <summary>Initializes a new instance of the <see cref="RowNode"/> class.</summary>
  /// <param name="rowSet">The rowset the row is one of.</param>
  public RowNode(RowSetNode rowSet) : base(NodeKind.Row) {
    ArgumentNullException.ThrowIfNull(rowSet);
    RowSet = rowSet;
  }

  /// <summary>Gets the rowset the row is one of.</summary>
  public RowSetNode RowSet { get; }

  /// <summary>Gets the entity the row is a row of, which is the rowset's.</summary>
  public ILinqEntity? Entity => RowSet.Entity;

  /// <inheritdoc/>
  public override string ToString() => $"Row of {RowSet}";
}

