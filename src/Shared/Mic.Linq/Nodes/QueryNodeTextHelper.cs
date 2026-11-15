using System.Linq.Expressions;
using System.Text;
using Mic.Linq.Translation;

namespace Mic.Linq.Nodes;

/// <summary>Renders a tree as readable text, for a test, a log line or a debugger watch, as
/// <see cref="QueryNode.AsText"/> produces it.</summary>
/// <remarks>The text is neither a serialization format nor SQL. The recursion is depth-guarded like every other
/// recursion over a tree.</remarks>
public static class QueryNodeTextHelper {

  /// <summary>Renders the whole subtree of a node as text.</summary>
  /// <param name="node">The root of the subtree.</param>
  /// <returns>The text.</returns>
  public static string GetText(QueryNode node) {
    ArgumentNullException.ThrowIfNull(node);
    var text = new StringBuilder();
    Write(node, text, 0);
    return text.ToString();
  }

  private static void Write(QueryNode node, StringBuilder text, int depth) {
    node.CheckDepth(depth);
    switch (node.Kind) {
      case NodeKind.EntitySet:
        text.Append(((EntitySetNode)node).Entity.Name);
        return;

      case NodeKind.Include: {
        var include = (IncludeNode)node;
        Write(include.Source, text, depth + 1);
        text.Append(" include ").Append(string.Join(", ", include.Includes));
        return;
      }

      case NodeKind.IgnoreFilters: {
        var ignore = (IgnoreFiltersNode)node;
        Write(ignore.Source, text, depth + 1);
        text.Append(' ').Append(ignore);
        return;
      }

      case NodeKind.TableFunction: {
        var function = (TableFunctionNode)node;
        text.Append(function.Table.Name).Append('(');
        for (var i = 0; i < function.Arguments.Count; i++) {
          if (i > 0)
            text.Append(", ");
          Write(function.Arguments[i], text, depth + 1);
        }
        text.Append(')');
        if (function.Alias != null)
          text.Append(' ').Append(function.Alias);
        return;
      }

      case NodeKind.JsonRows: {
        var rows = (JsonRowsNode)node;
        text.Append("rows(");
        if (rows.Parameter != null)
          text.Append('@').Append(rows.Parameter.Name);
        else
          Write(rows.Document, text, depth + 1);
        text.Append(": ").Append(string.Join(", ", rows.Specs.Select(c => c.Name))).Append(')');
        if (rows.Alias != null)
          text.Append(' ').Append(rows.Alias);
        return;
      }

      case NodeKind.Row:
        text.Append("row");
        return;

      case NodeKind.Member:
        WriteChild(node, text, 0, depth);
        text.Append('.').Append(node.Name);
        return;

      case NodeKind.Constant:
        var constantValue = ((ConstantNode)node).Value;
        text.Append(constantValue switch {
          null => "null",
          string s => "'" + s + "'",
          bool b => b ? "true" : "false",
          _ => constantValue.ToString(),
        });
        return;

      case NodeKind.Parameter:
        text.Append('@').Append(node.Name);
        return;

      case NodeKind.LogicalOp when ((LogicalOpNode)node).OpType == LogicalOpType.Not:
        text.Append("NOT ");
        WriteChild(node, text, 0, depth);
        return;

      case NodeKind.LogicalOp:
        WriteJoined(node, text, ((LogicalOpNode)node).OpType == LogicalOpType.And ? " AND " : " OR ", depth);
        return;

      // The SQL tree is written the way its SQL reads, so a test over it asserts structure.
      case NodeKind.Select: {
        var select = (SelectNode)node;
        // A select inside another is bracketed; the root is not.
        var asValue = depth > 0;
        if (asValue)
          text.Append('(');
        text.Append("SELECT ");
        if (select.IsDistinct)
          text.Append("DISTINCT ");
        if (select.SelectList.IsEmpty)
          text.Append('*');
        else
          WriteItems(select.SelectList, text, ", ", depth);
        text.Append(" FROM ");
        WriteItems(select.From, text, " ", depth);
        WriteClause(select.Where, text, " WHERE ", " AND ", depth);
        WriteClause(select.GroupBy, text, " GROUP BY ", ", ", depth);
        WriteClause(select.Having, text, " HAVING ", " AND ", depth);
        WriteClause(select.OrderBy, text, " ORDER BY ", ", ", depth);
        // The row numbers of spread pages trail the ordering, at the root only, as the statement writes them.
        if (!asValue)
          for (var i = 0; i < select.SpreadOrder.Count; i++) {
            text.Append(i == 0 && select.OrderBy.IsEmpty ? " ORDER BY " : ", ");
            Write(select.SpreadOrder[i], text, depth + 1);
          }
        WriteClause(select.Skip, text, " SKIP ", ", ", depth);
        WriteClause(select.Take, text, " TAKE ", ", ", depth);
        if (asValue)
          text.Append(')');
        return;
      }

      case NodeKind.Table: {
        var table = (TableNode)node;
        text.Append(table.Table.Name);
        if (table.Alias != null)
          text.Append(' ').Append(table.Alias);
        return;
      }

      case NodeKind.DerivedRowSet: {
        var derived = (DerivedRowSetNode)node;
        Write(derived.Select, text, depth + 1);
        if (derived.Alias != null)
          text.Append(' ').Append(derived.Alias);
        return;
      }

      case NodeKind.Join: {
        var join = (JoinNode)node;
        text.Append(join.JoinType switch { JoinType.LeftOuter => "LEFT JOIN ", JoinType.Cross => "CROSS JOIN ", _ => "JOIN " });
        WriteChild(node, text, 0, depth);
        if (join.JoinType == JoinType.Cross)
          return;
        text.Append(" ON ");
        WriteChild(node, text, 1, depth);
        return;
      }

      // A set operation is bracketed as a whole inside another select; its operands never are.
      case NodeKind.SetOperation: {
        var setOperation = (SetOperationNode)node;
        var asValue = depth > 0;
        if (asValue)
          text.Append('(');
        Write(setOperation.Left, text, 0);
        text.Append(setOperation.OpType switch {
          SetOperationType.Union => " UNION ",
          SetOperationType.Concat => " UNION ALL ",
          SetOperationType.Intersect => " INTERSECT ",
          _ => " EXCEPT ",
        });
        Write(setOperation.Right, text, 0);
        if (asValue)
          text.Append(')');
        if (setOperation.Alias != null)
          text.Append(' ').Append(setOperation.Alias);
        return;
      }

      case NodeKind.Column:
        text.Append(((ColumnNode)node).Row.RowSet.Alias ?? "?").Append('.').Append(node.Name);
        return;

      case NodeKind.SelectedValue:
        WriteChild(node, text, 0, depth);
        text.Append(" AS ").Append(node.Name);
        return;

      case NodeKind.Clause:
        WriteItems(node, text, ", ", depth);
        return;

      case NodeKind.Exists:
        // The select brackets itself, being inside another.
        text.Append("EXISTS ");
        WriteChild(node, text, 0, depth);
        return;

      case NodeKind.AggregateValue: {
        var aggregate = (AggregateValueNode)node;
        text.Append(aggregate.OpType == AggregateType.Average ? "AVG" : aggregate.OpType.ToString().ToUpperInvariant())
          .Append('(');
        if (aggregate.Term == null)
          text.Append('*');
        else
          Write(aggregate.Term, text, depth + 1);
        text.Append(')');
        return;
      }

      // A definition reads "name AS (body)", a use reads "name alias", as the statement writes them.
      case NodeKind.Cte: {
        var cte = (CteNode)node;
        text.Append(cte.CteName).Append(" AS (");
        Write(cte.Body, text, 0);
        text.Append(')');
        return;
      }

      case NodeKind.CteRef: {
        var reference = (CteRefNode)node;
        text.Append(reference.Cte.CteName);
        if (reference.Alias != null)
          text.Append(' ').Append(reference.Alias);
        return;
      }

      case NodeKind.RowNumber: {
        var number = (RowNumberNode)node;
        text.Append("ROW_NUMBER() OVER (");
        for (var i = 0; i < node.ChildCount; i++) {
          if (i == 0 && number.PartitionCount > 0)
            text.Append("PARTITION BY ");
          else if (i == number.PartitionCount)
            text.Append(i == 0 ? "ORDER BY " : " ORDER BY ");
          else
            text.Append(", ");
          Write(node.Children[i], text, depth + 1);
          if (i >= number.PartitionCount && node.Children[i].Flags.HasFlag(NodeFlags.Descending))
            text.Append(" DESC");
        }
        text.Append(')');
        return;
      }

      default:
        // A call reads as the method, then the source, then the arguments: Where(source, condition).
        if (node is MethodCallNode call) {
          text.Append(call.MethodName).Append('(');
          WriteChild(node, text, 0, depth);
          for (var i = 1; i < node.ChildCount; i++) {
            text.Append(", ");
            Write(node.Children[i], text, depth + 1);
            if (node.Children[i].Flags.HasFlag(NodeFlags.Descending))
              text.Append(" DESC");
          }
          text.Append(')');
          return;
        }
        // A null test reads as "(value IS NULL)".
        if (node is NamedOpNode { OpType: NamedOpType.IsNull or NamedOpType.IsNotNull } nullTest) {
          text.Append('(');
          WriteChild(node, text, 0, depth);
          text.Append(nullTest.OpType == NamedOpType.IsNull ? " IS NULL)" : " IS NOT NULL)");
          return;
        }
        // A binary operator, and any kind this method does not know, reads as "(left Kind right)".
        text.Append('(');
        WriteChild(node, text, 0, depth);
        text.Append(' ').Append(GetOperatorText(node)).Append(' ');
        WriteChild(node, text, 1, depth);
        text.Append(')');
        return;
    }
  }

  private static string GetOperatorText(QueryNode node) => node switch {
    BinaryNode binary => binary.NodeType switch {
      ExpressionType.Equal => "=",
      ExpressionType.NotEqual => "!=",
      ExpressionType.LessThan => "<",
      ExpressionType.LessThanOrEqual => "<=",
      ExpressionType.GreaterThan => ">",
      ExpressionType.GreaterThanOrEqual => ">=",
      ExpressionType.Add or ExpressionType.AddChecked => "+",
      ExpressionType.Subtract or ExpressionType.SubtractChecked => "-",
      ExpressionType.Multiply or ExpressionType.MultiplyChecked => "*",
      ExpressionType.Divide => "/",
      var other => other.ToString(),
    },
    NamedOpNode { OpType: NamedOpType.In } => "IN",
    SetOperationNode set => set.OpType.ToString(),
    _ => node.Kind.ToString(),
  };

  private static void WriteChild(QueryNode node, StringBuilder text, int index, int depth) {
    if (node.ChildAt(index) is { } child)
      Write(child, text, depth + 1);
    else
      text.Append('?');
  }

  /// <summary>Writes a clause after its keyword, or nothing where the clause is empty.</summary>
  private static void WriteClause(ClauseNode clause, StringBuilder text, string keyword, string separator, int depth) {
    if (clause.IsEmpty)
      return;
    text.Append(keyword);
    WriteItems(clause, text, separator, depth);
  }

  /// <summary>Writes the children separated, with an ordering term carrying its direction.</summary>
  private static void WriteItems(QueryNode node, StringBuilder text, string separator, int depth) {
    for (var i = 0; i < node.ChildCount; i++) {
      if (i > 0)
        text.Append(separator);
      Write(node.Children[i], text, depth + 1);
      if (node.Children[i].Flags.HasFlag(NodeFlags.Descending))
        text.Append(" DESC");
    }
  }

  private static void WriteJoined(QueryNode node, StringBuilder text, string separator, int depth) {
    text.Append('(');
    for (var i = 0; i < node.ChildCount; i++) {
      if (i > 0)
        text.Append(separator);
      Write(node.Children[i], text, depth + 1);
    }
    text.Append(')');
  }
}
