using System.Linq.Expressions;
using Mic.Linq;
using Mic.Linq.ExpressionTree;
using Mic.Linq.Nodes;
using Mic.Linq.Translation;

namespace Mic.Tests.Orm;

// CONVERSION, AND THE PROOF THAT IT DECIDES NOTHING: there is no model in this file. The converter turns a C#
// expression tree into the engine's own mutable tree and answers no question about what any of it MEANS, so a test
// of it needs no entities, no database and no application — and needing one would be the failure.
[TestClass]
public class ExpressionTranscriberTests {

  record Product(int Rank, string Name);

  [TestMethod]
  public void TranscribedTree_MirrorsTheExpression() {
    // ----- shape: one node per expression, in the compiler's own order ---------------------------------------------
    Expression<Func<Product, bool>> predicate = p => p.Rank > 3;
    var lambda = (LambdaNode)ExpressionTreeConverter.Convert(predicate);

    Assert.AreEqual(1, lambda.ParameterCount);
    Assert.AreEqual(NodeKind.Expression, lambda.Kind, "nothing is interpreted — every node is just an expression");
    var body = (ExpressionNode)lambda.Body;
    Assert.AreEqual(ExpressionType.GreaterThan, body.NodeType);

    // A MEMBER IS THE `MemberInfo` THE COMPILER RECORDED, not a model member: the converter has no model to
    // resolve one against, which is the whole point.
    var member = (MemberAccessNode)body.Children[0];
    Assert.AreEqual(nameof(Product.Rank), member.ClrMember!.Name);
    Assert.AreEqual(typeof(int), member.DataType);
    Assert.IsNotNull(member.Source, "the source expression is kept, so a refusal can quote what was written");

    // ----- a lambda parameter mentioned twice is ONE node ----------------------------------------------------------
    //
    // The identity IS the binding everywhere in this engine, and the untyped path gets it by handing a row to a
    // callback. Here it comes out of the conversion itself.
    Expression<Func<Product, bool>> twice = p => p.Rank > 3 && p.Name != null;
    var twiceLambda = (LambdaNode)ExpressionTreeConverter.Convert(twice);
    var parameter = twiceLambda.Children[0];
    var mentions = new List<QueryNode>();
    twiceLambda.ForEach(node => { if (ReferenceEquals(node, parameter)) mentions.Add(node); });
    Assert.AreEqual(3, mentions.Count, "the parameter node itself, and the same object at both mentions");

    // ----- a QUOTE is not a node ----------------------------------------------------------------------------------
    //
    // Every lambda handed to `Queryable` arrives wrapped in one, and it says nothing about the query. Dropped once,
    // here, rather than unwrapped at each place that reads a lambda.
    var quoted = Expression.Quote(predicate);
    Assert.IsInstanceOfType<LambdaNode>(ExpressionTreeConverter.Convert(quoted));

    // ----- `new { … }` keeps its SLOT NAMES -----------------------------------------------------------------------
    //
    // `NewExpression.Members` is the only place the pairing of argument to slot name exists; a projection's column
    // names and a transparent identifier's row names are both read out of it later.
    Expression<Func<Product, object>> projection = p => new { p.Rank, Label = p.Name };
    var made = (NewNode)((LambdaNode)ExpressionTreeConverter.Convert(projection)).Body;
    CollectionAssert.AreEqual(new[] { "Rank", "Label" }, made.SlotNames!.ToArray());
    Assert.AreEqual(2, made.ChildCount, "one child per constructor argument, in argument order");

    // ----- a CALL carries its method, and says whether it was written on an instance -------------------------------
    Expression<Func<Product, bool>> call = p => p.Name.StartsWith("x");
    var starts = (MethodCallNode)((LambdaNode)ExpressionTreeConverter.Convert(call)).Body;
    Assert.AreEqual(nameof(string.StartsWith), starts.Method.Name);
    Assert.IsTrue(starts.HasInstance, "`text.StartsWith(…)` is an instance call; `Queryable.Where(…)` is not");

    // ----- ARGUMENT numbering is not CHILD numbering, and this is the one place it bites --------------------------
    //
    // The walk visits a call's instance before its arguments, so an instance call's arguments are shifted by one
    // while a static call's are not. Getting it wrong makes a WRONG TREE rather than a compile error, which is why
    // both spellings are pinned here rather than left to each use site.
    Assert.AreEqual(1, starts.ArgumentCount, "the instance is not an argument");
    Assert.IsInstanceOfType<ConstantNode>(starts.Argument(0), "a constant is a logical leaf, made as one — nothing to decide");
    Assert.AreSame(starts.Children[0], starts.Receiver, "an instance call's receiver is the instance");

    // ----- A CAPTURED LOCAL IS COPIED AS THE READ THE COMPILER WROTE — a member access on its closure — and decided
    // nothing about: whether it is a value the caller brought is the translator's tiering pass's question.
    var min = 3;
    Expression<Func<Product, bool>> captured = p => p.Rank > min;
    var compare = (ExpressionNode)((LambdaNode)ExpressionTreeConverter.Convert(captured)).Body;
    var closureRead = (MemberAccessNode)compare.Children[1];
    Assert.IsInstanceOfType<ConstantNode>(closureRead.Target, "`min` is a member access on the compiler's closure, held as a constant");
    Assert.AreEqual(NodeFlags.None, compare.Flags, "and the converter marks nothing");
    Assert.IsInstanceOfType<ConstantNode>(starts.Argument(0), "a bare constant is a literal");

    // AND THE EXTENSION SPELLING ANSWERS THE SAME WAY, which is what `Unstatic` used to arrange one site at a time:
    // `Queryable.Where(source, predicate)` carries its receiver as argument ZERO.
    IQueryable<Product> source = new[] { new Product(1, "a") }.AsQueryable();
    var where = (MethodCallNode)ExpressionTreeConverter.Convert(source.Where(p => p.Rank > 0).Expression);
    Assert.IsFalse(where.HasInstance);
    Assert.AreEqual(2, where.ArgumentCount, "source and predicate");
    Assert.AreSame(where.Children[0], where.Receiver, "an extension call's receiver is argument zero");
    Assert.AreEqual(1, where.ReceiverArguments.Count, "…leaving the predicate after it");
    Assert.IsInstanceOfType<LambdaNode>(where.ReceiverArguments[0], "and the quote is already gone");
  }
}
