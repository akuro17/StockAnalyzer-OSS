using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// A scaler sidecar can arrive with an imported model, so it is read through <see cref="BoundedFileReader"/> with the <see cref="ModelSidecarLimits"/> ceiling
/// instead of being read whole. The reader counts the bytes it actually reads, so a file that grows while it is read is stopped too.
/// </summary>
public sealed class FixedScalerSidecarLimitTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sa_scaler_limit_" + Guid.NewGuid());

    public FixedScalerSidecarLimitTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, true); } catch { /* best-effort cleanup */ }
    }

    /// <summary>A stream that reports no usable length and keeps producing bytes: what a file that grows while it is read looks like to the reader.</summary>
    private sealed class EndlessStream : Stream
    {
        public long Produced { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => Produced; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            Array.Fill(buffer, (byte)' ', offset, count);
            Produced += count;
            return count;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private string WriteFile(string name, byte[] content)
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(100, 100)]
    [InlineData(81920, 81920)]       // exactly one reader chunk
    [InlineData(81921, 81921)]
    public void ReadAllBytes_FileAtTheCeiling_IsReadCompletely(int size, long ceiling)
    {
        byte[] content = Enumerable.Range(0, size).Select(i => (byte)(i % 251)).ToArray();

        byte[] read = BoundedFileReader.ReadAllBytes(WriteFile("a.bin", content), ceiling);

        Assert.Equal(content, read);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(101, 100)]
    [InlineData(81921, 81920)]
    public void ReadAllBytes_FileOneByteOverTheCeiling_Throws(int size, long ceiling)
    {
        string path = WriteFile("b.bin", new byte[size]);

        Assert.Throws<InvalidDataException>(() => BoundedFileReader.ReadAllBytes(path, ceiling));
    }

    [Theory]
    [InlineData(long.MaxValue)]
    [InlineData(long.MaxValue - 1)]
    public void ReadAllBytes_CeilingNearLongMaxValue_ReadsASmallStreamCompletely(long ceiling)
    {
        byte[] content = Enumerable.Range(0, 100).Select(i => (byte)i).ToArray();
        using var stream = new MemoryStream(content);

        Assert.Equal(content, BoundedFileReader.ReadAllBytes(stream, ceiling));
    }

    [Fact]
    public void ReadAllBytes_StreamThatNeverEnds_StopsAtTheCeilingPlusOneByte()
    {
        using var endless = new EndlessStream();

        Assert.Throws<InvalidDataException>(() => BoundedFileReader.ReadAllBytes(endless, 1000));

        Assert.Equal(1001, endless.Produced);
    }

    [Fact]
    public void ReadAllBytes_MissingFile_KeepsTheFileNotFoundException()
    {
        Assert.Throws<FileNotFoundException>(() => BoundedFileReader.ReadAllBytes(Path.Combine(_directory, "missing.bin"), 10));
    }

    [Fact]
    public void ReadAllBytes_NegativeCeiling_Throws()
    {
        using var stream = new MemoryStream();

        Assert.Throws<ArgumentOutOfRangeException>(() => BoundedFileReader.ReadAllBytes(stream, -1));
    }

    private static Dictionary<string, string> OhlcvMetadata(string modelFileName) => new()
    {
        ["feature_mode"] = "ohlcv_minmax", ["window_size"] = "2", ["channels"] = "5",
        ["normalization"] = "fixed_zscore", ["lags"] = "[]", ["clip_sigma"] = "",
        ["scaler_ref"] = modelFileName + ".scaler.json",
    };

    [Fact]
    public void Load_OversizedSidecar_ThrowsInvalidDataException()
    {
        string modelPath = Path.Combine(_directory, "m.onnx");
        File.WriteAllBytes(modelPath + ".scaler.json", new byte[ModelSidecarLimits.MaxFixedScalerBytes + 1]);

        var error = Assert.Throws<InvalidDataException>(() => FixedScaler.Load(modelPath, OhlcvMetadata("m.onnx")));

        Assert.Contains("permitted", error.Message);
    }

    [Fact]
    public void Load_NormalSidecar_StillLoadsAndValidates()
    {
        string modelPath = Path.Combine(_directory, "m.onnx");
        var metadata = OhlcvMetadata("m.onnx");
        var scaler = new FixedScaler(FixedScaler.CurrentSchemaVersion, "ohlcv_minmax", 2, Array.Empty<int>(), null,
            new[] { "lag0:open", "lag0:high", "lag0:low", "lag0:close", "lag0:volume" },
            Enumerable.Range(0, 5).Select(i => new FixedScalerStatistic(i, 1, -1, 1)).ToArray(),
            FixedScaler.ComputeContractHash(metadata));
        File.WriteAllText(modelPath + ".scaler.json",
            JsonSerializer.Serialize(scaler, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }));

        var loaded = FixedScaler.Load(modelPath, metadata);

        Assert.Equal(5, loaded.Statistics.Length);
        Assert.Equal(scaler.FeatureContractHash, loaded.FeatureContractHash);
    }

    [Fact]
    public void WorstCaseScalerSidecar_FitsInHalfOfTheCeiling()
    {
        // MaxChannels channels, 256-character identities, the longest double texts and the largest lag list.
        var statistics = Enumerable.Range(0, FeatureSpec.MaxChannels)
            .Select(_ => new FixedScalerStatistic(-double.MaxValue / 3, double.MaxValue / 3, -double.MaxValue / 7, double.MaxValue / 7)).ToArray();
        var identities = Enumerable.Range(0, FeatureSpec.MaxChannels).Select(i => $"lag{i}:" + new string('x', 256)).ToArray();
        var lags = Enumerable.Range(1, TrainingResourceOverrides.MaximumFeatureLag).ToArray();
        var scaler = new FixedScaler(FixedScaler.CurrentSchemaVersion, "composed_features", 512, lags, double.MaxValue / 3,
            identities, statistics, new string('f', 64));

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(scaler,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });

        Assert.True(json.Length * 2L <= ModelSidecarLimits.MaxFixedScalerBytes,
            $"worst-case scaler is {json.Length} bytes; the ceiling is {ModelSidecarLimits.MaxFixedScalerBytes}");
    }
}
