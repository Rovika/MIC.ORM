using System.Linq.Expressions;
using Mic.Linq.ExpressionTree;

namespace Mic.Tests.Orm;

// A SNAPSHOT OF AN EXPRESSION'S CAPTURED LOCALS: the tree reads copies of the closure objects made at the call, so an
// assignment after it does not reach the tree. The locals are copied, not what they refer to.
[TestClass]
public class CapturedLocalSnapshotterTests {
  sealed class Box {
    public int Number;
  }

  int _member = 1;

  static T Run<T>(Expression expression) => ((Expression<Func<T>>)expression).Compile()();

  [TestMethod]
  public void ALocalAssignedAfterTheSnapshot_IsReadAsItWas() {
    var rank = 1;
    var label = "a";
    Expression<Func<string>> read = () => label + rank;
    var snapshot = CapturedLocalSnapshotter.Snapshot(read);
    rank = 2;
    label = "b";
    Assert.AreEqual("a1", Run<string>(snapshot));
    Assert.AreEqual("b2", Run<string>(read), "the tree as written still reads the live locals");
    Assert.AreNotSame(read, snapshot);

    Expression<Func<int>> literal = () => 5;
    Assert.AreSame(literal, CapturedLocalSnapshotter.Snapshot(literal), "nothing captured, nothing rewritten");
  }

  [TestMethod]
  public void ALoopVariableAndALocalOfAnOuterScope_AreReadAsTheyWere() {
    // The lambda reads a local of the method and one of the loop: the loop's closure object refers to the method's,
    // and both are copied.
    var prefix = "p";
    var snapshots = new List<Expression>();
    for (var i = 0; i < 3; i++) {
      var doubled = i * 2;
      Expression<Func<string>> read = () => prefix + i + doubled;
      snapshots.Add(CapturedLocalSnapshotter.Snapshot(read));
    }
    prefix = "q";
    CollectionAssert.AreEqual(new[] { "p00", "p12", "p24" }, snapshots.Select(Run<string>).ToList());
  }

  [TestMethod]
  public void WhatALocalRefersTo_IsNotCopied() {
    var box = new Box { Number = 1 };
    Expression<Func<int>> readBox = () => box.Number;
    Expression<Func<int>> readMember = () => _member + box.Number;
    var boxSnapshot = CapturedLocalSnapshotter.Snapshot(readBox);
    var memberSnapshot = CapturedLocalSnapshotter.Snapshot(readMember);
    box.Number = 2;
    _member = 10;
    Assert.AreEqual(2, Run<int>(boxSnapshot), "the same object, read when the tree runs");
    Assert.AreEqual(12, Run<int>(memberSnapshot), "a member of this is read when the tree runs");
    box = new Box { Number = 7 };
    Assert.AreEqual(2, Run<int>(boxSnapshot), "the local itself was copied");
  }

  [TestMethod]
  public void ACapturedQuery_IsRebuiltOverTheCopies() {
    var level = 1;
    var numbers = new[] { 1, 2, 3 }.AsQueryable();
    var chosen = numbers.Where(n => n == level);
    Expression<Func<bool>> hasOne = () => chosen.Any(n => n == 1);
    var snapshot = CapturedLocalSnapshotter.Snapshot(hasOne);
    level = 2;
    Assert.IsTrue(Run<bool>(snapshot), "the query inside reads level as it was");
    Assert.IsFalse(Run<bool>(hasOne));
  }
}
