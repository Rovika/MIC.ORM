using Mic.Common;
using Mic.Linq.Model;

namespace Mic.Linq.Nodes;

/// <summary>Represents one SELECT, whose children are its eight clauses in <see cref="ClauseType"/> order, always
/// present and empty where the select has nothing to say in them.</summary>
/// <remarks>The select's <see cref="RowSetNode.Row"/> is a row of what it produces, which a select over it writes its
/// lambdas against; what it selects from is the first source in its FROM clause.</remarks>
public sealed class SelectNode : RowSetNode {
  /// <summary>Initializes a new instance of the <see cref="SelectNode"/> class over a source.</summary>
  /// <param name="source">The rowset the select reads from.</param>
  /// <param name="entity">The entity the select produces rows of, or <see langword="null"/> if not yet decided.</param>
  public SelectNode(RowSetNode source, ILinqEntity? entity) : base(NodeKind.Select, entity, null) {
    ArgumentNullException.ThrowIfNull(source);
    foreach (var type in Enum.GetValues<ClauseType>())
      AddChild(new ClauseNode(type));
    From.AddChild(source);
    source.OwnerSelect = this;
  }

  /// <summary>Gets a clause by type.</summary>
  /// <param name="type">One of the enumeration values that specifies the clause.</param>
  /// <returns>The clause.</returns>
  public ClauseNode Clause(ClauseType type) => (ClauseNode)Children[(int)type];

  /// <summary>Gets or sets a value that indicates whether the rows the select finds are held for writing until the
  /// transaction ends.</summary>
  /// <remarks>The value is set on the root select from <see cref="QueryOptions.LockForUpdate"/> and spelled by the
  /// SQL builder.</remarks>
  public bool LocksForUpdate { get; set; }

  /// <summary>Gets or sets a value that indicates whether the rows another transaction holds are skipped rather than
  /// waited for, under <see cref="LocksForUpdate"/>.</summary>
  /// <remarks>The value is set on the root select from <see cref="QueryOptions.SkipLocked"/>.</remarks>
  public bool SkipsLocked { get; set; }

  /// <summary>Gets the clause of output values.</summary>
  /// <remarks>An empty list means the whole row: every stored member of the <see cref="ResultSet"/>'s entity. Once
  /// anything is named here, what is not named is not exposed.</remarks>
  public ClauseNode SelectList => Clause(ClauseType.Select);
  /// <summary>Gets the FROM clause: the source first, then the joins.</summary>
  public ClauseNode From => Clause(ClauseType.From);
  /// <summary>Gets the WHERE clause.</summary>
  public ClauseNode Where => Clause(ClauseType.Where);
  /// <summary>Gets the GROUP BY clause.</summary>
  public ClauseNode GroupBy => Clause(ClauseType.GroupBy);
  /// <summary>Gets the HAVING clause.</summary>
  public ClauseNode Having => Clause(ClauseType.Having);
  /// <summary>Gets the ORDER BY clause.</summary>
  public ClauseNode OrderBy => Clause(ClauseType.OrderBy);
  /// <summary>Gets the clause that holds the number of rows to skip.</summary>
  public ClauseNode Skip => Clause(ClauseType.Skip);
  /// <summary>Gets the clause that holds the number of rows to take.</summary>
  public ClauseNode Take => Clause(ClauseType.Take);

  /// <summary>Gets the rowset the select reads from, which is the first source in its FROM clause.</summary>
  public RowSetNode Source => (RowSetNode)From.Children[0];

  /// <summary>Gets or sets the rowset whose rows the select's result is rows of: the source, unless a join moved the
  /// result to the far table, as a many-to-many list does.</summary>
  public RowSetNode ResultSet {
    get => _resultSet ?? Source;
    set => _resultSet = value;
  }
  RowSetNode? _resultSet;

  /// <summary>Gets or sets a value that indicates whether duplicate rows are removed.</summary>
  public bool IsDistinct { get; set; }

  /// <summary>Gets or sets how far along its clauses the select has been built.</summary>
  /// <remarks>The translator reads the stage to decide when the chain has gone backwards and the select has to become
  /// a derived table.</remarks>
  public SelectStage Stage { get; set; } = SelectStage.Open;

  /// <summary>Gets the keys of a grouped select, as named output values, which the group key and its members bind
  /// to.</summary>
  /// <remarks>The GROUP BY clause holds the expressions grouped by, which for an entity key are its key columns; this
  /// holds what the caller named.</remarks>
  public List<SelectedValueNode> Keys { get; private set; } = [];

  /// <summary>Gets a value that indicates whether the select groups, so that its <see cref="RowSetNode.Row"/> is a
  /// row of a group.</summary>
  public bool IsGrouped => Keys.Count > 0;

  /// <summary>Gets or sets the element selector of a grouping, which an aggregate written without a selector
  /// aggregates, or <see langword="null"/> if the grouping has none.</summary>
  public LambdaNode? GroupElement { get; set; }

  /// <summary>Gets or sets the name of the method by which the caller asked for one row, such as <c>First</c> or
  /// <c>SingleOrDefault</c>, or <see langword="null"/> if the caller did not.</summary>
  /// <remarks>The reader uses the name to decide whether an empty result is an error and whether a second row is one.
  /// </remarks>
  public string? OneRow { get; set; }

  /// <summary>Gets the row numbers of the pages spread into the select, in the order they were joined, which the
  /// statement ends its ordering with after whatever the caller ordered by.</summary>
  /// <remarks>The row numbers are not in the ORDER BY clause, since they are not an ordering the caller wrote. They
  /// are written only where the select is the statement's own; inside a derived table a trailing ordering decides
  /// nothing.</remarks>
  public List<ColumnNode> SpreadOrder { get; private set; } = [];

  /// <summary>Gets the row of the result: the source's row for a select of whole rows, or a row of the projected shape
  /// whose members are the output values in order.</summary>
  public RowNode ResultRow => Row;

  /// <inheritdoc/>
  protected internal override void RelinkClone(QueryTreeCloner cloner) {
    base.RelinkClone(cloner);
    _resultSet = cloner.MapOrNull(_resultSet);
    Keys = cloner.MapAll(Keys);
    GroupElement = cloner.MapOrNull(GroupElement);
    SpreadOrder = cloner.MapAll(SpreadOrder);
  }

  /// <inheritdoc/>
  public override string ToString() => $"SELECT FROM {Source}";
}

/// <summary>Represents one clause of a select, with its items as children.</summary>
public sealed class ClauseNode : QueryNode {
  /// <summary>Initializes a new instance of the <see cref="ClauseNode"/> class.</summary>
  /// <param name="type">One of the enumeration values that specifies the clause.</param>
  public ClauseNode(ClauseType type) : base(NodeKind.Clause) => ClauseType = type;

  /// <summary>Gets which clause of a select this is.</summary>
  public ClauseType ClauseType { get; }

  /// <summary>Gets a value that indicates whether the clause has no items.</summary>
  public bool IsEmpty => !HasChildren;

  /// <inheritdoc/>
  protected internal override ulong AddCustomNodeHash(ulong hash) => Fnv1aHash.Combine(hash, (ulong)ClauseType);

  /// <inheritdoc/>
  public override string ToString() => ClauseType.ToString().ToUpperInvariant();
}

/// <summary>Represents a table in a FROM clause: the mapping's table for an entity, and the entity it holds.</summary>
public sealed class TableNode : RowSetNode {
  /// <summary>Initializes a new instance of the <see cref="TableNode"/> class.</summary>
  /// <param name="table">The table.</param>
  public TableNode(ILinqTable table) : base(NodeKind.Table, table.Entity, null) {
    ArgumentNullException.ThrowIfNull(table);
    Table = table;
  }

  /// <summary>Gets the table.</summary>
  public ILinqTable Table { get; }

  /// <inheritdoc/>
  protected internal override ulong AddCustomNodeHash(ulong hash) =>
    Fnv1aHash.Combine(Fnv1aHash.Combine(hash, Table.Schema ?? ""), Table.Name);

  /// <inheritdoc/>
  public override string ToString() => Alias == null ? Table.Name : $"{Table.Name} {Alias}";
}

/// <summary>Represents a call of a function of the server that returns rows, in a FROM clause: the mapping's table
/// of the function's row entity, and the arguments as the children.</summary>
/// <remarks>The table carries the function's name, schema and columns; the arguments are values, parameters or
/// constants, written into the call as the dialect spells one.</remarks>
public sealed class TableFunctionNode : RowSetNode {
  /// <summary>Initializes a new instance of the <see cref="TableFunctionNode"/> class.</summary>
  /// <param name="table">The table of the function's rows.</param>
  /// <param name="arguments">The arguments of the call, in the function's parameter order.</param>
  public TableFunctionNode(ILinqTable table, params QueryNode[] arguments)
      : base(NodeKind.TableFunction, table.Entity, null, arguments) {
    ArgumentNullException.ThrowIfNull(table);
    Table = table;
  }

  /// <summary>Gets the table of the function's rows, whose name is the function's.</summary>
  public ILinqTable Table { get; }

  /// <summary>Gets the arguments of the call.</summary>
  public IReadOnlyList<QueryNode> Arguments => Children;

  /// <inheritdoc/>
  protected internal override ulong AddCustomNodeHash(ulong hash) =>
    Fnv1aHash.Combine(Fnv1aHash.Combine(hash, Table.Schema ?? ""), Table.Name);

  /// <inheritdoc/>
  public override string ToString() => Alias == null ? $"{Table.Name}()" : $"{Table.Name}() {Alias}";
}

/// <summary>Represents a select used as a source, a derived table, whose first child is the select and whose rows are
/// the select's result rows.</summary>
public sealed class DerivedRowSetNode : RowSetNode {
  /// <summary>Initializes a new instance of the <see cref="DerivedRowSetNode"/> class.</summary>
  /// <param name="select">The select.</param>
  public DerivedRowSetNode(SelectNode select) : base(NodeKind.DerivedRowSet, select.Entity, null, select) { }

  /// <summary>Gets the select.</summary>
  public SelectNode Select => (SelectNode)Children[0];

  /// <inheritdoc/>
  public override string ToString() => Alias == null ? "(derived)" : $"(derived) {Alias}";
}

/// <summary>Represents a common table expression: a named query the statement defines once in its <c>WITH</c> list
/// and reads by name. The first child is the body, a select, or for a recursive one the anchor and the recursive part
/// as one <c>UNION ALL</c>.</summary>
/// <remarks>Definitions belong to the statement, not to a select, and are kept on the translation in the order they
/// were completed, which is dependency order. A recursive one names its columns up front, the level first and then
/// every stored member, since its recursive part reads it while it is being built; <see cref="Columns"/> is how a
/// reader finds one.</remarks>
public sealed class CteNode : RowSetNode {
  /// <summary>Initializes a new instance of the <see cref="CteNode"/> class.</summary>
  /// <param name="name">The name the common table expression is defined and read under.</param>
  /// <param name="body">The body.</param>
  /// <param name="cteKind">One of the enumeration values that specifies how the server is asked to treat it.</param>
  public CteNode(string name, RowSetNode body, CteKind cteKind) : base(NodeKind.Cte, body.Entity, null, body) {
    ArgumentException.ThrowIfNullOrEmpty(name);
    CteName = name;
    CteKind = cteKind;
  }

  /// <summary>Gets the name the common table expression is defined and read under.</summary>
  /// <remarks>This is not <see cref="QueryNode.Name"/>, which is the alias slot.</remarks>
  public string CteName { get; }
  /// <summary>Gets how the server is asked to treat the common table expression.</summary>
  public CteKind CteKind { get; }
  /// <summary>Gets the body.</summary>
  public RowSetNode Body => (RowSetNode)Children[0];

  /// <summary>Gets or sets the output values of a recursive common table expression, in order, the level first; or
  /// <see langword="null"/> for a plain one.</summary>
  public IReadOnlyList<SelectedValueNode>? Columns { get; set; }
  /// <summary>Gets a value that indicates whether the common table expression is recursive.</summary>
  public bool IsRecursive => Columns != null;

  /// <summary>Gets or sets the hash of the body as it was defined, before any reader projected a value into it.
  /// </summary>
  /// <remarks>A second definition under the same name is checked against this key.</remarks>
  public ulong ShapeKey { get; set; }

  /// <summary>Gets the reads of the common table expression by its own recursive part while it is being built.
  /// </summary>
  /// <remarks>Exactly one such read is the only form every server accepts.</remarks>
  public List<CteRefNode> SelfReferences { get; private set; } = [];

  /// <summary>Replaces the body.</summary>
  /// <param name="body">The new body.</param>
  public void ReplaceBody(RowSetNode body) => Children[0] = body;

  /// <inheritdoc/>
  protected internal override void RelinkClone(QueryTreeCloner cloner) {
    base.RelinkClone(cloner);
    Columns = Columns == null ? null : cloner.MapAll(Columns);
    SelfReferences = cloner.MapAll(SelfReferences);
  }

  /// <inheritdoc/>
  protected internal override ulong AddCustomNodeHash(ulong hash) =>
    Fnv1aHash.Combine(Fnv1aHash.Combine(hash, CteName), (ulong)CteKind);

  /// <inheritdoc/>
  public override string ToString() => $"WITH {CteName}";
}

/// <summary>Represents a common table expression read in a FROM clause: its name and an alias. This class cannot be
/// inherited.</summary>
/// <remarks>The node points at its definition; neither is a child of the other.</remarks>
public sealed class CteRefNode : RowSetNode {
  /// <summary>Initializes a new instance of the <see cref="CteRefNode"/> class.</summary>
  /// <param name="cte">The definition read.</param>
  public CteRefNode(CteNode cte) : base(NodeKind.CteRef, cte.Entity, null) {
    ArgumentNullException.ThrowIfNull(cte);
    Cte = cte;
  }

  /// <summary>Gets the definition read.</summary>
  public CteNode Cte { get; private set; }

  /// <inheritdoc/>
  protected internal override ulong AddCustomNodeHash(ulong hash) => Fnv1aHash.Combine(hash, Cte.CteName);

  /// <inheritdoc/>
  protected internal override void RelinkClone(QueryTreeCloner cloner) {
    base.RelinkClone(cloner);
    Cte = cloner.Map(Cte);
  }

  /// <inheritdoc/>
  public override string ToString() => Alias == null ? Cte.CteName : $"{Cte.CteName} {Alias}";
}

/// <summary>Represents a rowset joined into a FROM clause: the first child is the rowset, the second the condition it
/// is joined on.</summary>
/// <remarks>The condition is a child so that every pass walks it, as it holds columns and parameters like any other
/// condition.</remarks>
public sealed class JoinNode : QueryNode {
  /// <summary>Initializes a new instance of the <see cref="JoinNode"/> class.</summary>
  /// <param name="target">The rowset joined in.</param>
  /// <param name="condition">The condition the rowset is joined on.</param>
  /// <param name="type">One of the enumeration values that specifies how the rowset is joined.</param>
  /// <param name="member">The reference or list member the join was made for, or <see langword="null"/> for one the
  /// caller wrote.</param>
  public JoinNode(RowSetNode target, QueryNode condition, JoinType type, ILinqEntityMember? member = null)
      : base(NodeKind.Join, target, condition) {
    JoinType = type;
    Member = member;
  }

  /// <summary>Gets the rowset joined in.</summary>
  public RowSetNode Target => (RowSetNode)Children[0];
  /// <summary>Gets the condition the rowset is joined on.</summary>
  public QueryNode Condition => Children[1];
  /// <summary>Gets or sets how the rowset is joined.</summary>
  public JoinType JoinType { get; set; }

  /// <summary>Gets the reference or list member the join was made for, or <see langword="null"/> for one the caller
  /// wrote.</summary>
  public ILinqEntityMember? Member { get; }

  /// <inheritdoc/>
  protected internal override ulong AddCustomNodeHash(ulong hash) => Fnv1aHash.Combine(hash, (ulong)JoinType);

  /// <inheritdoc/>
  public override string ToString() => $"{(JoinType == JoinType.LeftOuter ? "LEFT JOIN" : "JOIN")} {Target}";
}

/// <summary>Represents a column of a row: the column the mapping names for a member of a table's row, or the output
/// value of the select a derived table wraps.</summary>
/// <remarks>The node points at the row it is read off and is not a child of it. The rowset's alias and the column's
/// name are the whole of what the SQL writes.</remarks>
public sealed class ColumnNode : QueryNode {
  /// <summary>Initializes a new instance of the <see cref="ColumnNode"/> class for a column of a table's row.</summary>
  /// <param name="row">The row the column is read off.</param>
  /// <param name="column">The mapping's column.</param>
  public ColumnNode(RowNode row, ILinqColumn column) : base(NodeKind.Column) {
    ArgumentNullException.ThrowIfNull(row);
    ArgumentNullException.ThrowIfNull(column);
    Row = row;
    Column = column;
    Name = column.Name;
    DataType = column.DataType;
  }

  /// <summary>Initializes a new instance of the <see cref="ColumnNode"/> class for a column of a derived table: the
  /// output value of the select it wraps, under the name it was selected as.</summary>
  /// <param name="row">The row the column is read off.</param>
  /// <param name="selected">The output value.</param>
  public ColumnNode(RowNode row, SelectedValueNode selected) : base(NodeKind.Column) {
    ArgumentNullException.ThrowIfNull(row);
    ArgumentNullException.ThrowIfNull(selected);
    Row = row;
    Selected = selected;
    Name = selected.Name;
    DataType = selected.DataType;
  }

  /// <summary>Initializes a new instance of the <see cref="ColumnNode"/> class for a column of a rowset that names its
  /// columns itself, such as the rows of a parameter, with nothing of the mapping.</summary>
  /// <param name="row">The row the column is read off.</param>
  /// <param name="member">The member whose name and type the column takes.</param>
  public ColumnNode(RowNode row, ILinqEntityMember member) : base(NodeKind.Column) {
    ArgumentNullException.ThrowIfNull(row);
    ArgumentNullException.ThrowIfNull(member);
    Row = row;
    Name = member.Name;
    DataType = member.DataType;
  }

  /// <summary>Gets the row the column is read off.</summary>
  public RowNode Row { get; private set; }

  /// <summary>Gets the mapping's column, or <see langword="null"/> for a column of a derived table or of a rowset that
  /// names its columns itself.</summary>
  public ILinqColumn? Column { get; }

  /// <summary>Gets the output value of a derived table's select, or <see langword="null"/> for any other column.
  /// </summary>
  public SelectedValueNode? Selected { get; private set; }

  /// <inheritdoc/>
  protected internal override ulong AddCustomNodeHash(ulong hash) => Fnv1aHash.Combine(hash, Name ?? "");

  /// <inheritdoc/>
  protected internal override void RelinkClone(QueryTreeCloner cloner) {
    Row = cloner.Map(Row);
    Selected = cloner.MapOrNull(Selected);
  }

  /// <inheritdoc/>
  public override string ToString() => $"{Row.RowSet.Alias ?? "?"}.{Name}";
}

/// <summary>Represents an <c>EXISTS (select)</c> condition, whose first child is the select. This class cannot be
/// inherited.</summary>
/// <remarks>A list asked whether it has any row becomes a sub-query rather than a join, since a one-to-many joined in
/// would multiply the rows it was asked about.</remarks>
public sealed class ExistsNode : QueryNode {
  /// <summary>Initializes a new instance of the <see cref="ExistsNode"/> class.</summary>
  /// <param name="select">The select.</param>
  public ExistsNode(SelectNode select) : base(NodeKind.Exists, select) => DataType = typeof(bool);

  /// <summary>Gets the select.</summary>
  public SelectNode Select => (SelectNode)Children[0];

  /// <inheritdoc/>
  public override string ToString() => "EXISTS";
}

/// <summary>Represents an aggregate on a select list: <c>COUNT(*)</c> with no term, or <c>SUM</c>, <c>MIN</c>,
/// <c>MAX</c> or <c>AVG</c> over the first child.</summary>
/// <remarks>A count is <c>COUNT(*)</c>, never <c>COUNT(column)</c>, which counts only the rows where the column is
/// not <see langword="null"/>.</remarks>
public sealed class AggregateValueNode : QueryNode {
  /// <summary>Initializes a new instance of the <see cref="AggregateValueNode"/> class.</summary>
  /// <param name="opType">One of the enumeration values that specifies the aggregate.</param>
  /// <param name="term">The term aggregated, or <see langword="null"/> for a count of rows.</param>
  /// <param name="dataType">The type of the value, or <see langword="null"/> if not known.</param>
  public AggregateValueNode(AggregateType opType, QueryNode? term, Type? dataType) : base(NodeKind.AggregateValue) {
    if (opType == AggregateType.None)
      throw new ArgumentException("An aggregate has to say which one it is.", nameof(opType));
    OpType = opType;
    if (term != null)
      AddChild(term);
    DataType = dataType;
  }

  /// <summary>Gets which aggregate this is.</summary>
  public AggregateType OpType { get; }
  /// <summary>Gets the term aggregated, or <see langword="null"/> for a count of rows.</summary>
  public QueryNode? Term => ChildAt(0);

  /// <inheritdoc/>
  protected internal override ulong AddCustomNodeHash(ulong hash) => Fnv1aHash.Combine(hash, (ulong)OpType);

  /// <inheritdoc/>
  public override string ToString() => OpType.ToString();
}

/// <summary>Represents <c>ROW_NUMBER() OVER (PARTITION BY … ORDER BY …)</c>: the rows of each partition numbered
/// from one in the ordering given. The children are the partition terms, then the ordering terms, a descending one
/// flagged as in an ORDER BY.</summary>
/// <remarks>This is how a page per outer row is written in SQL every server has: the rows are numbered within each
/// owner in a derived table, and the numbers the page asks for are kept outside it.</remarks>
public sealed class RowNumberNode : QueryNode {
  /// <summary>Initializes a new instance of the <see cref="RowNumberNode"/> class.</summary>
  /// <param name="partitionBy">The partition terms.</param>
  /// <param name="orderBy">The ordering terms, at least one.</param>
  public RowNumberNode(IEnumerable<QueryNode> partitionBy, IEnumerable<QueryNode> orderBy) : base(NodeKind.RowNumber) {
    ArgumentNullException.ThrowIfNull(partitionBy);
    ArgumentNullException.ThrowIfNull(orderBy);
    foreach (var term in partitionBy)
      AddChild(term);
    PartitionCount = ChildCount;
    foreach (var term in orderBy)
      AddChild(term);
    if (ChildCount == PartitionCount)
      throw new ArgumentException("Rows are numbered IN AN ORDER — a row number needs at least one ordering term.", nameof(orderBy));
    DataType = typeof(long);
  }

  /// <summary>Gets the number of children that are partition terms; the rest are the ordering terms.</summary>
  public int PartitionCount { get; }

  /// <inheritdoc/>
  protected internal override ulong AddCustomNodeHash(ulong hash) => Fnv1aHash.Combine(hash, (ulong)PartitionCount);

  /// <inheritdoc/>
  public override string ToString() => "ROW_NUMBER";
}

/// <summary>Represents a DELETE statement: the first child is the table whose rows go, the second the WHERE clause of
/// conditions over the table's own row, and the third, where the rows are found by key through a query, the join to
/// that query's derived table on the key.</summary>
/// <remarks>The node is a root only; a statement is not a value. The SQL builder spells the join as <c>key IN
/// (select)</c>, an <c>EXISTS</c> for a composite key, or the join itself where a server wants it that way.</remarks>
public sealed class DeleteNode : QueryNode {
  /// <summary>Initializes a new instance of the <see cref="DeleteNode"/> class with an empty WHERE clause.</summary>
  /// <param name="target">The table whose rows go.</param>
  public DeleteNode(TableNode target) : base(NodeKind.Delete, target, new ClauseNode(ClauseType.Where)) {
    ArgumentNullException.ThrowIfNull(target);
  }

  /// <summary>Gets the table whose rows go.</summary>
  public TableNode Target => (TableNode)Children[0];
  /// <summary>Gets the WHERE clause.</summary>
  public ClauseNode Where => (ClauseNode)Children[1];

  /// <summary>Gets the query the rows are found in, as a derived table selecting the key, joined on it; or
  /// <see langword="null"/> if the WHERE clause alone says which rows.</summary>
  public JoinNode? Source => ChildCount > 2 ? (JoinNode)Children[2] : null;

  /// <inheritdoc/>
  public override string ToString() => $"DELETE FROM {Target}";
}

/// <summary>Represents an UPDATE statement: the first child is the table, the second the SET list, the third the
/// WHERE clause, and the fourth, where the values come out of a query, the join to that query's derived table on the
/// key.</summary>
/// <remarks>Each item of the SET list is a <see cref="SelectedValueNode"/> whose name is the member written and whose
/// source is the value. Where the values come out of a query, the target's own filter rides in the join condition and
/// the WHERE clause is empty.</remarks>
public sealed class UpdateNode : QueryNode {
  /// <summary>Initializes a new instance of the <see cref="UpdateNode"/> class with an empty SET list and WHERE
  /// clause.</summary>
  /// <param name="target">The table.</param>
  public UpdateNode(TableNode target)
      : base(NodeKind.Update, target, new ClauseNode(ClauseType.Select), new ClauseNode(ClauseType.Where)) {
    ArgumentNullException.ThrowIfNull(target);
  }

  /// <summary>Gets the table.</summary>
  public TableNode Target => (TableNode)Children[0];
  /// <summary>Gets the SET list.</summary>
  public ClauseNode Set => (ClauseNode)Children[1];
  /// <summary>Gets the WHERE clause.</summary>
  public ClauseNode Where => (ClauseNode)Children[2];

  /// <summary>Gets the query the values are read out of, joined on the key; or <see langword="null"/> if the values
  /// are expressions over the target's own row.</summary>
  public JoinNode? Source => ChildCount > 3 ? (JoinNode)Children[3] : null;

  /// <inheritdoc/>
  public override string ToString() => $"UPDATE {Target}";
}

/// <summary>Represents an <c>INSERT … SELECT</c> statement: the first child is the table, the second the select whose
/// rows go in, and <see cref="Members"/> the target's members the select's output values are written to.</summary>
/// <remarks>A reference among the projection's slots has become its foreign-key columns by the time the node is
/// made.</remarks>
public sealed class InsertNode : QueryNode {
  /// <summary>Initializes a new instance of the <see cref="InsertNode"/> class.</summary>
  /// <param name="target">The table.</param>
  /// <param name="rows">The select whose rows go in.</param>
  /// <param name="members">The target's members, one per selected value, in the select list's order.</param>
  public InsertNode(TableNode target, SelectNode rows, IReadOnlyList<ILinqEntityMember> members)
      : base(NodeKind.Insert, target, rows) {
    ArgumentNullException.ThrowIfNull(members);
    if (members.Count != rows.SelectList.ChildCount)
      throw new ArgumentException("An insert writes one member per selected value.", nameof(members));
    Members = members;
  }

  /// <summary>Gets the table.</summary>
  public TableNode Target => (TableNode)Children[0];
  /// <summary>Gets the select whose rows go in.</summary>
  public SelectNode Rows => (SelectNode)Children[1];
  /// <summary>Gets the target's members the select's output values are written to, in the select list's order.
  /// </summary>
  public IReadOnlyList<ILinqEntityMember> Members { get; }

  /// <inheritdoc/>
  public override string ToString() => $"INSERT INTO {Target}";
}

/// <summary>Represents one output value of a select: the first child is what it selects, and the name is what it is
/// selected as.</summary>
/// <remarks>The select list is built on demand, by the reader asking for what it reads and by an outer select naming
/// a value of a derived table; each ask makes one node, once, and a column of the derived table points at it.</remarks>
public sealed class SelectedValueNode : QueryNode {
  /// <summary>Initializes a new instance of the <see cref="SelectedValueNode"/> class.</summary>
  /// <param name="source">What the value selects.</param>
  /// <param name="name">The name the value is selected as.</param>
  public SelectedValueNode(QueryNode source, string name) : base(NodeKind.SelectedValue, source) {
    ArgumentException.ThrowIfNullOrEmpty(name);
    Name = name;
    DataType = source.DataType;
  }

  /// <summary>Gets what the value selects.</summary>
  public QueryNode Source => Children[0];

  /// <inheritdoc/>
  public override string ToString() => $"{Source} AS {Name}";
}
