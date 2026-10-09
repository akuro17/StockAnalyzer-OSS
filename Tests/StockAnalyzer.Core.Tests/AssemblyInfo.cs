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
