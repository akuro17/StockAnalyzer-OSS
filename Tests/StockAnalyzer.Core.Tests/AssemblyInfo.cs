using Xunit;

namespace StockAnalyzer.Core.Tests;

// Test classes that construct a real PythonService (embedded Python.Runtime engine / named-pipe
// subprocess, e.g. PythonProcessManager.StartAsync) race for the same pipe/process resources
// when xUnit's default cross-class parallelization runs them concurrently, which can time out
// or hang PythonProcessManager.StartAsync. Grouping them into this collection serializes only
// those classes with each other while the rest of the assembly keeps running in parallel.
[CollectionDefinition("PythonIpc", DisableParallelization = true)]
public class PythonIpcCollection
{
}

// Test classes that start a real child process (the training Python environment) run in this collection, alone, never beside
// another test. Windows' CreateProcess, which Process.Start uses with redirected streams, hands the child every INHERITABLE handle
// of this process, and native libraries open files that way (OnnxRuntime reads a model through a C runtime descriptor). A child
// started while another test is loading an ONNX model therefore keeps that model file locked for as long as it runs, and the other
// test's cleanup (Directory.Delete or Directory.Move) fails with "being used by another process" (R2-5; the holder was identified
// as the Python child of PreparedSourcePipelineParityTests, whose parent was this test host).
// ChildProcessCollectionGuardTests enforces this: a test class in this project that calls a process starter must carry the attribute, or another
// collection defined with DisableParallelization (a class can have only one collection, e.g. the portfolio tests that also change an environment variable).
[CollectionDefinition("ChildProcess", DisableParallelization = true)]
public class ChildProcessCollection
{
}

// Test classes with a test that holds every thread-pool thread on purpose (to prove an order that must not depend on the pool) run in this collection, alone,
// never beside another test: for the time of the hold every other test that needs a pool thread would otherwise stall (intermittent full-suite failures).
[CollectionDefinition("ThreadPoolStarvation", DisableParallelization = true)]
public class ThreadPoolStarvationCollection
{
}

// Test classes with a test that computes for minutes on one thread (the geometric formation detectors: 100 s and 287 s alone) or that measures allocation and needs a
// quiet process run in this collection, after all parallel tests: a class of the parallel phase that is still running keeps the whole phase open, so no serial
// collection can start, and a measurement beside allocation-heavy tests retries for tens of seconds (FiveThousandBars: 7 s alone, 87 s in the full run).
// Measured 2026-11-18 with the stall probe (owner decision: serial collection, not opt-in and not a shorter input).
[CollectionDefinition("LongRunningMeasurement", DisableParallelization = true)]
public class LongRunningMeasurementCollection
{
}
