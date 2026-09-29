using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

// PredictionService constructs a native ONNX Runtime InferenceSession; running these tests
// concurrently with each other risks native session/handle contention across xUnit's
// parallel test classes, so they are serialized within this collection.
[CollectionDefinition("Non-Parallel ONNX Tests", DisableParallelization = true)]
public class NonParallelOnnxTestsCollection
{
}
