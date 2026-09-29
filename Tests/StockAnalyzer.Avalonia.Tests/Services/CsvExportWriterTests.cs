using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Avalonia.Services;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Services;

public class CsvExportWriterTests
{
    [Fact]
    public void EscapeField_PlainValue_ReturnsUnchanged()
    {
        Assert.Equal("AAPL", CsvExportWriter.EscapeField("AAPL"));
    }

    [Fact]
    public void EscapeField_ValueContainingComma_IsQuoted()
    {
        Assert.Equal("\"Alphabet, Inc.\"", CsvExportWriter.EscapeField("Alphabet, Inc."));
    }

    [Fact]
    public void EscapeField_ValueContainingQuote_DoublesEmbeddedQuote()
    {
        Assert.Equal("\"Say \"\"Hi\"\"\"", CsvExportWriter.EscapeField("Say \"Hi\""));
    }

    [Fact]
    public void EscapeField_ValueContainingNewline_IsQuoted()
    {
        Assert.Equal("\"line1\nline2\"", CsvExportWriter.EscapeField("line1\nline2"));
    }

    [Fact]
    public void EscapeField_Null_ReturnsEmptyString()
    {
        Assert.Equal(string.Empty, CsvExportWriter.EscapeField(null));
    }

    [Fact]
    public void FormatAndEscapeValue_Decimal_UsesInvariantCultureRegardlessOfCurrentCulture()
    {
        var original = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE"); // uses ',' as decimal separator
            Assert.Equal("123.456", CsvExportWriter.FormatAndEscapeValue(123.456m));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
        }
    }

    [Fact]
    public void FormatAndEscapeValue_Null_ReturnsEmptyString()
    {
        Assert.Equal(string.Empty, CsvExportWriter.FormatAndEscapeValue(null));
    }

    [Fact]
    public async Task WriteRowsAsync_WritesCommaJoinedRowsWithLfNewlineTerminators()
    {
        var tempFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"csv_writer_test_{System.Guid.NewGuid():N}.csv");
        try
        {
            await CsvExportWriter.WriteRowsAsync(tempFile, new[]
            {
                new[] { "Symbol", "Close" },
                new[] { "AAPL", "123.45" },
            });

            var content = await System.IO.File.ReadAllTextAsync(tempFile);
            Assert.Equal("Symbol,Close\nAAPL,123.45\n", content);
        }
        finally
        {
            if (System.IO.File.Exists(tempFile)) System.IO.File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task WriteRowsAsync_DoesNotWriteUtf8Bom()
    {
        // Project standard is BOM-free UTF-8 (SA_ARCHITECTURE_RULES.md); a BOM would show up as
        // the 3 bytes EF BB BF before the content, and would break a naive Python/Pandas read of
        // this export (this feature's own stated re-import/external-tooling purpose).
        var tempFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"csv_writer_test_{System.Guid.NewGuid():N}.csv");
        try
        {
            await CsvExportWriter.WriteRowsAsync(tempFile, new[] { new[] { "A" } });

            var bytes = await System.IO.File.ReadAllBytesAsync(tempFile);
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "File must not start with a UTF-8 BOM.");
        }
        finally
        {
            if (System.IO.File.Exists(tempFile)) System.IO.File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task WriteRowsAsync_DoesNotLeaveTemporaryFileBehindAfterSuccess()
    {
        var tempFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"csv_writer_test_{System.Guid.NewGuid():N}.csv");
        try
        {
            await CsvExportWriter.WriteRowsAsync(tempFile, new[] { new[] { "A" } });

            Assert.True(System.IO.File.Exists(tempFile));
            Assert.False(System.IO.File.Exists(tempFile + ".tmp"));
        }
        finally
        {
            if (System.IO.File.Exists(tempFile)) System.IO.File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task WriteRowsAsync_ExistingTargetFile_IsOverwritten()
    {
        var tempFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"csv_writer_test_{System.Guid.NewGuid():N}.csv");
        try
        {
            await System.IO.File.WriteAllTextAsync(tempFile, "stale content that must not survive");

            await CsvExportWriter.WriteRowsAsync(tempFile, new[] { new[] { "fresh" } });

            var content = await System.IO.File.ReadAllTextAsync(tempFile);
            Assert.Equal("fresh\n", content);
        }
        finally
        {
            if (System.IO.File.Exists(tempFile)) System.IO.File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task WriteLinesAsync_WritesLfTerminatedLinesWithNoDelimiterOrEscaping()
    {
        var tempFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"csv_writer_test_{System.Guid.NewGuid():N}.txt");
        try
        {
            await CsvExportWriter.WriteLinesAsync(tempFile, new[] { "AAPL", "MSFT" });

            var content = await System.IO.File.ReadAllTextAsync(tempFile);
            Assert.Equal("AAPL\nMSFT\n", content);
        }
        finally
        {
            if (System.IO.File.Exists(tempFile)) System.IO.File.Delete(tempFile);
        }
    }

    [Fact]
    public void FormatAndEscapeValue_DateTime_UsesIso8601RoundTripFormat_NotCultureGeneralFormat()
    {
        var value = new System.DateTime(2026, 9, 28, 15, 34, 22, System.DateTimeKind.Utc);
        Assert.Equal("2026-09-28T15:34:22.0000000Z", CsvExportWriter.FormatAndEscapeValue(value));
    }

    [Fact]
    public void FormatAndEscapeValue_DateTimeOffset_UsesIso8601RoundTripFormat()
    {
        var value = new System.DateTimeOffset(2026, 9, 28, 15, 34, 22, System.TimeSpan.Zero);
        Assert.Equal("2026-09-28T15:34:22.0000000+00:00", CsvExportWriter.FormatAndEscapeValue(value));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void FormatAndEscapeValue_NonFiniteDouble_ReturnsEmptyCellLikeNull(double value)
    {
        Assert.Equal(string.Empty, CsvExportWriter.FormatAndEscapeValue(value));
    }

    [Fact]
    public void FormatAndEscapeValue_FiniteDouble_FormatsNormally()
    {
        Assert.Equal("12.5", CsvExportWriter.FormatAndEscapeValue(12.5d));
    }

    [Theory]
    [InlineData("=HYPERLINK(\"https://example.com\")")]
    [InlineData("+1")]
    [InlineData("@SUM")]
    [InlineData("-1+1")]
    public void FormatAndEscapeValue_StringStartingWithFormulaTrigger_GetsNeutralizingApostrophePrefix(string value)
    {
        var result = CsvExportWriter.FormatAndEscapeValue(value);
        Assert.StartsWith("'", result.TrimStart('"'));
    }

    [Fact]
    public void FormatAndEscapeValue_NegativeDecimal_IsNotTreatedAsFormulaInjection()
    {
        // A leading '-' is a legitimate negative number for a numeric column - only genuinely
        // string-typed source values get the apostrophe guard, never a formatted number.
        Assert.Equal("-5.2", CsvExportWriter.FormatAndEscapeValue(-5.2m));
    }

    [Fact]
    public void FormatAndEscapeValue_OrdinaryString_IsUnchanged()
    {
        Assert.Equal("Apple Inc.", CsvExportWriter.FormatAndEscapeValue("Apple Inc."));
    }
}
