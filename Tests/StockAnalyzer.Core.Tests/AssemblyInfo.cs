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
