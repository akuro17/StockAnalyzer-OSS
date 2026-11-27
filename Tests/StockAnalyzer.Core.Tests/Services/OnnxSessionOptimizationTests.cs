using System;
using System.IO;
using Microsoft.ML.OnnxRuntime;
using Moq;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

public sealed class OnnxSessionOptimizationTests
{
    private static readonly string FixtureOnnx = Path.Combine("Assets", "attention_fixture.onnx");

    [Fact]
    public void PredictionSettings_Validate_AcceptsDefaultsAndNullThreads()
    {
        var defaults = new PredictionSettings();
        Assert.Equal(1, defaults.IntraOpNumThreads);
        Assert.Equal(1, defaults.InterOpNumThreads);
        Assert.False(defaults.AllowIntraOpSpinning);
        defaults.Validate();

        var nullThreads = new PredictionSettings { IntraOpNumThreads = null, InterOpNumThreads = null, AllowIntraOpSpinning = true };
        nullThreads.Validate();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65)]
    public void PredictionSettings_Validate_RejectsInvalidIntraOpThreads(int invalid)
    {
        var settings = new PredictionSettings { IntraOpNumThreads = invalid };
        Assert.Throws<InvalidOperationException>(() => settings.Validate());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(17)]
    public void PredictionSettings_Validate_RejectsInvalidInterOpThreads(int invalid)
    {
        var settings = new PredictionSettings { InterOpNumThreads = invalid };
        Assert.Throws<InvalidOperationException>(() => settings.Validate());
    }

    [Fact]
    public void OnnxSessionOptionsFactory_LoadsRealFixtureInBothModes()
    {
        using var inferOpts = OnnxSessionOptionsFactory.CreateInferenceOptions();
        using var inferSession = new InferenceSession(FixtureOnnx, inferOpts);
        Assert.NotEmpty(inferSession.InputMetadata);

        using var inspectOpts = OnnxSessionOptionsFactory.CreateMetadataInspectionOptions();
        using var inspectSession = new InferenceSession(FixtureOnnx, inspectOpts);
        Assert.Equal(inferSession.ModelMetadata.CustomMetadataMap.Count, inspectSession.ModelMetadata.CustomMetadataMap.Count);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(65, 1)]
    [InlineData(1, 0)]
    [InlineData(1, 17)]
    public void OnnxSessionOptionsFactory_OutOfRangeMockThreads_ThrowsArgumentOutOfRangeException(int? intra, int? inter)
    {
        var mock = new Mock<IStockAnalyzerSettings>();
        mock.SetupGet(s => s.PredictionIntraOpNumThreads).Returns(intra);
        mock.SetupGet(s => s.PredictionInterOpNumThreads).Returns(inter);
        Assert.Throws<ArgumentOutOfRangeException>(() => OnnxSessionOptionsFactory.CreateInferenceOptions(mock.Object));
    }

    [Fact]
    public void DisposableSessionCache_DefersEvictedAndDisposedCleanupUntilActiveUsersRelease()
    {
        var cache = new DisposableSessionCache<string, TrackedDisposable>(capacity: 2, StringComparer.Ordinal);
        int factoryCalls = 0;

        var first = new TrackedDisposable();
        var second = new TrackedDisposable();
        var third = new TrackedDisposable();

        using (var leaseA = cache.AcquireOrCreate("k1", () => { factoryCalls++; return first; }))
        {
            Assert.Same(first, leaseA.Value);

            using (var leaseA2 = cache.AcquireOrCreate("k1", () => throw new InvalidOperationException("must hit cache")))
            {
                Assert.Same(first, leaseA2.Value);
            }

            // Insert "k2", then re-touch "k1" so "k2" becomes the LRU tail victim before inserting "k3".
            using (var leaseB = cache.AcquireOrCreate("k2", () => { factoryCalls++; return second; }))
            {
                using (cache.AcquireOrCreate("k1", () => throw new InvalidOperationException("must hit cache"))) { }

                // Keep leaseB held while adding "k3" so "k2" is evicted while still active!
                using (var leaseC = cache.AcquireOrCreate("k3", () => { factoryCalls++; return third; }))
                {
                    Assert.False(second.IsDisposed); // deferred while leaseB active
                    Assert.False(first.IsDisposed);
                }
                Assert.False(second.IsDisposed); // still held by leaseB
            }

            // Once leaseB exits, retired "k2" is immediately disposed.
            Assert.True(second.IsDisposed);
            Assert.False(first.IsDisposed);
            Assert.False(third.IsDisposed);

            // Disposing cache while leaseA is still held defers "k1" disposal, disposes idle "k3" immediately.
            cache.Dispose();
            Assert.True(third.IsDisposed);
            Assert.False(first.IsDisposed);
        }

        Assert.True(first.IsDisposed);
        Assert.Equal(3, factoryCalls);
    }

    private sealed class TrackedDisposable : IDisposable
    {
        public bool IsDisposed { get; private set; }
        public void Dispose() => IsDisposed = true;
    }
}
