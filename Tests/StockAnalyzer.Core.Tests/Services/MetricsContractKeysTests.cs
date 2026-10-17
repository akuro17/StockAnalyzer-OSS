using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Tests.TestHelpers;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// The training writes <c>.onnx.metrics.json</c> and the application reads it (quality hint, confidence reach, automatic-activation gate). A key renamed on one
/// side only would make the reader see "unknown" and silently disable those features, so each key the reader uses is pinned to the Python source that writes it.
/// </summary>
public sealed class MetricsContractKeysTests
{
    [Fact]
    public void TheConfidenceReachObjectIsWrittenUnderTheKeyTheReaderUses()
        => Assert.Contains($"release_report[\"{ModelConfidenceReach.Key}\"]", TrainingSources.Read("run_training.py"));

    [Fact]
    public void TheHistogramFieldsAreWrittenUnderTheNamesTheReaderUses()
    {
        var metrics = TrainingSources.Read("metrics.py");

        Assert.Contains($"\"{ModelConfidenceReach.BinsKey}\": bins", metrics);
        Assert.Contains($"\"{ModelConfidenceReach.CountKey}\": int(", metrics);
        Assert.Contains($"\"{ModelConfidenceReach.HistogramKey}\": [", metrics);
    }

    [Fact]
    public void TheHeldOutLogLossIsWrittenUnderTheKeyTheQualityCheckReads()
        => Assert.Contains($"\"{ModelQualityCheck.LogLossKey}\": float(", TrainingSources.Read("run_training.py"));
}
