
// CLASS-LEVEL PARALLELISM IS WHAT MAKES THE SHAPE PAY. One [TestClass] per vendor means classes-in-parallel is
// vendors-in-parallel, with the tests inside a vendor running one at a time — which is what is wanted anyway, since
// they share a server and must not race for it.
//
// Wall clock then lands near the SLOWEST vendor rather than the sum of all of them. A console runner invoking the
// suite once per database cannot do this; it serialises by construction.
[assembly: Parallelize(Workers = 0, Scope = ExecutionScope.ClassLevel)]
