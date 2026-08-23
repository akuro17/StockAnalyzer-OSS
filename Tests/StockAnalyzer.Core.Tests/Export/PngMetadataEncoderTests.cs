using System;
using System.Text;
using StockAnalyzer.Core.Models.Export;
using StockAnalyzer.Core.Services.Export;
using Xunit;

namespace StockAnalyzer.Core.Tests.Export;

public class PngMetadataEncoderTests
{
    // Minimal valid 1x1 PNG byte array
    private static readonly byte[] MinimalValidPng =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, // PNG signature
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52, // IHDR header
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
        0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4, 0x89,
        0x00, 0x00, 0x00, 0x0A, 0x49, 0x44, 0x41, 0x54, // IDAT chunk
        0x78, 0x9C, 0x63, 0x00, 0x01, 0x00, 0x00, 0x05, 0x00, 0x01,
        0x0D, 0x0A, 0x2D, 0xB4,
        0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, // IEND chunk
        0xAE, 0x42, 0x60, 0x82
    ];

    [Fact]
    public void InjectMetadata_WithValidPng_InjectsItxtChunks()
    {
        var metadata = new ChartImageMetadata
        {
            Symbol = "7203",
            CompanyName = "トヨタ自動車",
            Timeframe = "Daily",
            StartDate = new DateTime(2025, 1, 1),
            EndDate = new DateTime(2026, 8, 20),
            IndicatorsSummary = "SMA(20), RSI(14)",
            GeneratedAt = new DateTime(2026, 8, 20, 11, 0, 0, DateTimeKind.Utc)
        };

        var result = PngMetadataEncoder.InjectMetadata(MinimalValidPng, metadata);

        Assert.NotNull(result);
        Assert.True(result.Length > MinimalValidPng.Length);

        // Verify PNG signature is still intact
        Assert.Equal(0x89, result[0]);
        Assert.Equal(0x50, result[1]);
        Assert.Equal(0x4E, result[2]);
        Assert.Equal(0x47, result[3]);

        // Verify iTXt text contains Japanese and Symbol
        var resultText = Encoding.UTF8.GetString(result);
        Assert.Contains("7203", resultText);
        Assert.Contains("トヨタ自動車", resultText);
        Assert.Contains("Daily", resultText);
        Assert.Contains("SMA(20), RSI(14)", resultText);
        Assert.Contains("iTXt", resultText);
    }

    [Fact]
    public void InjectMetadata_WithInvalidPng_ReturnsOriginalBytes()
    {
        byte[] invalidBytes = [1, 2, 3, 4, 5];
        var metadata = new ChartImageMetadata { Symbol = "AAPL" };

        var result = PngMetadataEncoder.InjectMetadata(invalidBytes, metadata);

        Assert.Equal(invalidBytes, result);
    }
}
