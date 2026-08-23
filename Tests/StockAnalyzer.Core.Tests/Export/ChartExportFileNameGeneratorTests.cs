using System;
using StockAnalyzer.Core.Models.Export;
using StockAnalyzer.Core.Services.Export;
using Xunit;

namespace StockAnalyzer.Core.Tests.Export;

public class ChartExportFileNameGeneratorTests
{
    [Fact]
    public void GenerateFileName_WithSymbolAndCompany_ReturnsFormattedString()
    {
        var date = new DateTime(2026, 8, 20);
        var result = ChartExportFileNameGenerator.GenerateFileName("AAPL", "Apple Inc.", date, ImageExportFormat.Png);

        Assert.Equal("AAPL-Apple_Inc-2026-08-20.png", result);
    }

    [Fact]
    public void GenerateFileName_WithJapaneseCompanyName_PreservesUtf8Characters()
    {
        var date = new DateTime(2026, 8, 20);
        var result = ChartExportFileNameGenerator.GenerateFileName("7203", "トヨタ自動車", date, ImageExportFormat.Png);

        Assert.Equal("7203-トヨタ自動車-2026-08-20.png", result);
    }

    [Fact]
    public void GenerateFileName_WhenCompanyEmptyOrSameAsSymbol_OmitsCompany()
    {
        var date = new DateTime(2026, 8, 20);
        var result1 = ChartExportFileNameGenerator.GenerateFileName("MSFT", "", date, ImageExportFormat.Jpeg);
        var result2 = ChartExportFileNameGenerator.GenerateFileName("MSFT", "msft", date, ImageExportFormat.Webp);

        Assert.Equal("MSFT-2026-08-20.jpg", result1);
        Assert.Equal("MSFT-2026-08-20.webp", result2);
    }

    [Fact]
    public void SanitizeComponent_RemovesForbiddenChars()
    {
        var raw = "ABC/DEF:GHI*JKL?MNO\"PQR<STU>VWX|YZ";
        var sanitized = ChartExportFileNameGenerator.SanitizeComponent(raw);

        Assert.DoesNotContain("/", sanitized);
        Assert.DoesNotContain("\\", sanitized);
        Assert.DoesNotContain(":", sanitized);
        Assert.DoesNotContain("*", sanitized);
        Assert.DoesNotContain("?", sanitized);
        Assert.DoesNotContain("\"", sanitized);
        Assert.DoesNotContain("<", sanitized);
        Assert.DoesNotContain(">", sanitized);
        Assert.DoesNotContain("|", sanitized);
    }

    [Fact]
    public void EnsureNotReserved_PrefixesReservedWindowsNames()
    {
        Assert.Equal("_CON", ChartExportFileNameGenerator.EnsureNotReserved("CON"));
        Assert.Equal("_prn", ChartExportFileNameGenerator.EnsureNotReserved("prn"));
        Assert.Equal("_NUL", ChartExportFileNameGenerator.EnsureNotReserved("NUL"));
        Assert.Equal("NORMAL_FILE", ChartExportFileNameGenerator.EnsureNotReserved("NORMAL_FILE"));
    }
}
