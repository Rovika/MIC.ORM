namespace Mic.Tests.Orm.Db;

/// <summary>Drops every shared schema when the run ends.</summary>
/// <remarks>ITS OWN `[TestClass]` BECAUSE MSTEST REQUIRES ONE. `[AssemblyCleanup]` is only discovered inside a class
/// marked `[TestClass]`, and it used to sit on `DmlTests` — which is ABSTRACT and carries no such attribute, since the
/// concrete per-vendor classes in `Vendors.cs` are the test classes. So it was never called, and every shared database
/// every run ever created was left on the server: forty-six `dml_*` schemas had accumulated on MySQL alone before
/// anyone counted, with the same on the others.
///
/// That is not only untidiness. Each of those is a schema the server carries in its dictionary for good, and the
/// vendor it hurts most is the one that was already the suite's whole wall clock — Oracle, where creating a user is
/// SECONDS and gets no faster for having hundreds of them about.
///
/// A class with no tests in it, which is the price of the attribute being where MSTest looks. Nothing else belongs
/// here: a hook that runs once per ASSEMBLY has no business knowing about a particular suite beyond calling it.</remarks>
[TestClass]
public static class SuiteCleanup {
  [AssemblyCleanup]
  public static Task DropSharedDatabasesAsync() => DmlTests.DropSharedDatabasesAsync();
}
