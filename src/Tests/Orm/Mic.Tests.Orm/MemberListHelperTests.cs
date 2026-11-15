using Mic.Orm.Model.Construction;

namespace Mic.Tests.Orm;

// The one syntax every member list in the attribute vocabulary is written in. Worth testing hard: it is the only
// place a member is named as a STRING, so a mistake here is a wrong model rather than a compile error.
[TestClass]
public class MemberListHelperTests {
  private static MemberListResult Parse(string spec, bool allowDirection = true) =>
    MemberListHelper.Parse(spec, "test", allowDirection);

  private static string Render(MemberListResult r) => string.Join(" ", r.Members);

  // Fourteen methods, each one line over a pure function, became three: what the syntax ACCEPTS, what it still accepts
  // but no longer wants, and what it REFUSES.

  [TestMethod]
  public void MemberListSyntax_IsNamesWithADashMarker() {
    // ----- a plain list is all ascending ----------------------------------------------------------------------------
    var plain = Parse("LastName,FirstName");
    Assert.AreEqual("LastName FirstName", Render(plain));
    Assert.AreEqual(0, plain.Errors.Count);
    Assert.AreEqual(0, plain.Warnings.Count);

    // ----- and a dash marks the member AFTER it ----------------------------------------------------------------------
    var descending = Parse("LastName -FirstName DateOfBirth");
    Assert.AreEqual("LastName -FirstName DateOfBirth", Render(descending));
    Assert.AreEqual(0, descending.Warnings.Count);

    // ----- whitespace is insignificant padding ------------------------------------------------------------------------
    //
    // The rule that makes the compact URL form and a readable attribute the SAME syntax: separator runs collapse.
    foreach (var spec in new[] { "a-b", "a - b", "a -b", "a,-b", "a ,  - b" }) {
      var parsed = Parse(spec);
      Assert.AreEqual("a -b", Render(parsed), $"spec: '{spec}'");
      Assert.AreEqual(0, parsed.Errors.Count, $"spec: '{spec}'");
    }

    // ----- '+' and space are equivalent --------------------------------------------------------------------------------
    //
    // Why this matters: in application/x-www-form-urlencoded a '+' MEANS space, and ASP.NET's query parser decodes it
    // that way. Declaring them equivalent turns that rewrite into a no-op instead of corruption.
    Assert.AreEqual(Render(Parse("a+b+c")), Render(Parse("a b c")));
    Assert.AreEqual("a b -c", Render(Parse("a+b-c")));

    // ----- and a separator at either end is tolerated ---------------------------------------------------------------------
    var padded = Parse(",a,b,");
    Assert.AreEqual("a b", Render(padded));
    Assert.AreEqual(0, padded.Errors.Count);
  }

  [TestMethod]
  public void VitaForms_StillParseAndWarn() {
    // Three spellings, one rule: they work, and they say they are on the way out.
    foreach (var (spec, expected) in new[] {
      ("Name-asc,DateCreated-desc", "Name -DateCreated"),        // the suffix form
      ("LastName:ASC,FirstName:DESC", "LastName -FirstName"),    // the colon form
      ("Name-asc;DateCreated-desc", "Name -DateCreated"),        // the semicolon separator
    }) {
      var parsed = Parse(spec);
      Assert.AreEqual(expected, Render(parsed), spec);
      Assert.AreEqual(0, parsed.Errors.Count, spec);
      Assert.IsTrue(parsed.Warnings.Any(w => w.Contains("deprecated")), spec);
    }

    // ----- and the two forms bind in OPPOSITE directions ------------------------------------------------------------
    //
    // The ambiguity that made 'asc'/'desc' reserved words: '-X' marks the member AFTER the dash, 'X-desc' the one
    // BEFORE the word. Without the reservation, "Name-desc" would read as two members.
    Assert.AreEqual("-FirstName", Render(Parse("-FirstName")));
    Assert.AreEqual("-Name", Render(Parse("Name-desc")));

    var reserved = Parse("Name-desc");
    Assert.AreEqual(1, reserved.Members.Count, "a direction word is not a member name");
    Assert.AreEqual("Name", reserved.Members[0].Name);
  }

  [TestMethod]
  public void InvalidMemberList_IsAnError() {
    Assert.IsTrue(Parse("desc,Name").HasErrors, "a direction word with no member");
    Assert.IsTrue(Parse("Name,name").HasErrors, "the same member twice");

    // Empty in each of the ways it can be empty.
    Assert.IsTrue(Parse("").HasErrors);
    Assert.IsTrue(Parse("   ").HasErrors);
    Assert.IsTrue(Parse(" , - ").HasErrors);
    Assert.IsTrue(MemberListHelper.Parse(null, "test").HasErrors);

    // ----- and a direction where direction is MEANINGLESS ------------------------------------------------------------
    //
    // An index's included members, a many-to-many's refs: a '-' there is reported rather than silently ignored.
    Assert.IsTrue(Parse("a,-b", allowDirection: false).HasErrors);
    Assert.IsTrue(Parse("a-desc", allowDirection: false).HasErrors);
    Assert.IsFalse(Parse("a,b,c", allowDirection: false).HasErrors);
  }
}
