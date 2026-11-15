using Mic.Linq;
using Mic.Linq.ExpressionTree;
using Mic.Linq.Model;
using Mic.Linq.Nodes;
using Mic.Linq.Translation;
using Mic.Orm;
using Mic.Orm.Db;
using Mic.Orm.Db.Postgres;
using Mic.Orm.Runtime;
using BeautyShop;

namespace Mic.Tests.Orm;

// A CLONE IS A SECOND TREE OF THE SAME SHAPE: no node shared with the original, every reference between nodes pointing
// at the copy, and what the original shared, such as one row under many columns, shared the same way in the copy.
// Checked on the SQL tree, which is the most tangled form a tree takes, and on a recursive common table expression,
// whose step reads the definition it is part of.
[TestClass]
public class QueryTreeCloneTests {
  static (EntityApp App, EntitySession Session, ILinqDataModel Model) Setup() {
    var app = new PlainBeautyShopApp();
    app.CollectingLog();
    app.TryInit();
    Assert.IsTrue(app.Initialized, app.Report());
    AppInitHelper.CreateDatabase(app, new DbSettings(new PostgresProvider(), "Host=nowhere.invalid;Database=none;Username=none;Password=none"));
    return (app, new EntitySession(new OperationContext(app)), app.MappedModel());
  }

  static TranslatedQuery Translate(IQueryable query, ILinqDataModel model) =>
    QueryTranslator.Translate(ExpressionTreeScanner.Scan(query.Expression), ExpressionTreeConverter.Convert(query.Expression), model);

  static HashSet<QueryNode> NodesOf(QueryNode root) {
    var nodes = new HashSet<QueryNode>(ReferenceEqualityComparer.Instance);
    root.ForEach(node => nodes.Add(node));
    return nodes;
  }

  [TestMethod]
  public void Clone_IsASecondTreeOfTheSameShape() {
    var (_, session, model) = Setup();
    var minRate = 20m;
    var query = session.EntitySet<IEmployee>()
      .Where(e => e.HourlyRate >= minRate && e.HomeShop.Name != "Closed")
      .OrderBy(e => e.HiredOn).Take(10)
      .Select(e => new { e.Id, Shop = e.HomeShop.Name });
    var original = Translate(query, model).SqlRoot;
    var clone = original.Clone();

    Assert.AreEqual(original.AsText, clone.AsText, "the same statement");
    var originals = NodesOf(original);
    var copies = NodesOf(clone);
    Assert.AreEqual(originals.Count, copies.Count);
    Assert.IsFalse(copies.Overlaps(originals), "no node is shared with the original");

    // Every link beside the children points into the copy, and the sharing is the original's: the columns read off one
    // table share one row, which is that table's.
    var rowSets = copies.OfType<RowSetNode>().ToList();
    foreach (var rowSet in rowSets)
      Assert.IsTrue(rowSet.OwnerSelect == null || copies.Contains(rowSet.OwnerSelect), $"{rowSet}: owner select in the copy");
    foreach (var column in copies.OfType<ColumnNode>()) {
      Assert.IsTrue(copies.Contains(column.Row.RowSet), $"{column}: row of a rowset in the copy");
      Assert.AreSame(column.Row.RowSet.Row, column.Row, $"{column}: the rowset's own row");
      Assert.IsTrue(column.Selected == null || copies.Contains(column.Selected), $"{column}: selected value in the copy");
    }
    var select = (SelectNode)clone;
    Assert.IsTrue(copies.Contains(select.ResultSet), "the result set is the copy's");
    Assert.IsTrue(select.SpreadOrder.Count == 0 || select.SpreadOrder.All(copies.Contains));
  }

  [TestMethod]
  public void Clone_FollowsARecursiveCteBackToItself() {
    var (_, session, model) = Setup();
    var query = session.EntitySet<IEmployee>().Where(e => e.HourlyRate > 10)
      .AsRecursiveCte("chain", found => found.Where(e => e.Manager != null).Select(e => e.Manager!), maxDepth: 7)
      .Select(e => e.HiredOn);
    var cte = Translate(query, model).Ctes.Single();
    var clone = (CteNode)cte.Clone();

    Assert.AreEqual(cte.AsText, clone.AsText);
    Assert.AreEqual(1, clone.SelfReferences.Count);
    var selfReference = clone.SelfReferences[0];
    Assert.AreSame(clone, selfReference.Cte, "the copy's step reads the copy");
    Assert.IsTrue(NodesOf(clone).Contains(selfReference), "…and the read is inside the copy's body");
    Assert.AreSame(cte, cte.SelfReferences[0].Cte, "the original is untouched");
    Assert.IsNotNull(clone.Columns);
    Assert.IsTrue(clone.Columns!.All(NodesOf(clone).Contains), "the recursive columns are the copy's output values");
  }

  [TestMethod]
  public void Template_StandsItsBodyOverTheRowItIsGiven() {
    var (_, _, model) = Setup();
    var employee = model.FindEntity("Employee")!;
    var table = model.FindTable(employee)!;
    var placeholder = new TableNode(table);
    var rate = table.Columns.First(c => c.Member?.Name == "HourlyRate");
    // A condition prepared over a placeholder row, as an extension's filter is.
    var prepared = QueryMaker.Greater(new ColumnNode(placeholder.Row, rate), QueryMaker.Constant(10m));
    var template = new QueryTreeTemplate(prepared, new Dictionary<string, QueryNode> { [QueryTreeTemplate.RowPlaceholder] = placeholder.Row });

    var actual = new TableNode(table) { Alias = "t7" };
    var condition = template.Instantiate(QueryTreeTemplate.RowPlaceholder, actual.Row);

    var column = (ColumnNode)((BinaryNode)condition).Left;
    Assert.AreSame(actual.Row, column.Row, "the column reads the row it was stood over");
    Assert.AreSame(placeholder.Row, ((ColumnNode)((BinaryNode)prepared).Left).Row, "the body still reads the placeholder");
    Assert.AreNotSame(prepared, condition);
    Assert.AreEqual("(t7.HourlyRate > 10)", condition.AsText);

    // Every placeholder is replaced, and nothing else is named.
    Assert.ThrowsExactly<ArgumentException>(() => template.Instantiate(new Dictionary<string, QueryNode>()));
    Assert.ThrowsExactly<ArgumentException>(() => template.Instantiate("table", actual));
  }
}
