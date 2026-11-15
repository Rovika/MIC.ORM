
// CLASS-LEVEL PARALLELISM, the same arrangement the other two db suites use: one [TestClass] per vendor makes
// classes-in-parallel vendors-in-parallel, with the tests inside a vendor running one at a time — which is what is
// wanted anyway, since they share a schema and its seeded rows.
[assembly: Parallelize(Workers = 0, Scope = ExecutionScope.ClassLevel)]
