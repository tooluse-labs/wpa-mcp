// xUnit runs each test *class* in its own collection, in parallel by default. Several
// of our tests open the same fixtures/small_cpu.etl through TraceCache, and each parallel
// path independently calls TraceLog.OpenOrConvert — which writes a shared
// "<basename>.etlx.new" temp file before atomic-renaming it to ".etlx". The races clobber
// each other's temp file ("being used by another process" / "could not find .etlx.new").
//
// Disabling assembly-wide parallelization is sufficient: per-test runtime against the
// 60 MB fixture is sub-second, so serialized execution still finishes the suite in a
// few seconds.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
