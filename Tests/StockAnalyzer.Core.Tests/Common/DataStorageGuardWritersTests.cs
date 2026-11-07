using System;
using System.Collections.Immutable;
using System.IO;
using System.Threading.Tasks;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Backtest.Reporting;
using Xunit;
using static StockAnalyzer.TestSupport.TestDataStorageState;

namespace StockAnalyzer.Core.Tests.Common;

/// <summary>
/// D-2' Run 3b / S4: the writers that put their product below the data folder refuse before touching anything when the folder is read-only
/// (templates, the default-folder report export). The export that goes to a folder the user picked is never refused.
/// </summary>
public class DataStorageGuardWritersTests
{
    // ---- templates -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task TemplateService_ReadOnly_RefusesSaveAndDelete_BeforeAnyValidationOrFileAccess()
    {
        using var service = new TemplateService(null, ReadOnlyState());

        // A null template would be an ArgumentNullException; the guard comes first, so nothing else was looked at.
        var save = await Assert.ThrowsAsync<DataStorageReadOnlyException>(() => service.SaveAsync<StockAnalyzer.Core.Models.Templates.TemplateBase>(null!));
        var delete = await Assert.ThrowsAsync<DataStorageReadOnlyException>(() => service.DeleteAsync(default, Guid.NewGuid()));

        Assert.Equal(TemplateService.SaveOperation, save.Operation);
        Assert.Equal(TemplateService.DeleteOperation, delete.Operation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TemplateService_NoStateOrWritable_ValidatesAsBefore(bool withWritableState)
    {
        using var service = withWritableState ? new TemplateService(null, WritableState()) : new TemplateService();

        await Assert.ThrowsAsync<ArgumentNullException>(() => service.SaveAsync<StockAnalyzer.Core.Models.Templates.TemplateBase>(null!));
    }

    // ---- backtest report export -------------------------------------------------------------------------------------

    private static ImmutableArray<BacktestTrade> NoTrades => ImmutableArray<BacktestTrade>.Empty;

    [Fact]
    public async Task ReportExporter_ReadOnly_RefusesTheDefaultFolderExport_ButNotTheFolderTheUserPicked()
    {
        var exporter = new BacktestReportExporter(null, ReadOnlyState());
        var picked = Path.Combine(Path.GetTempPath(), "sa_ro_export_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(picked);
        try
        {
            var refused = await Assert.ThrowsAsync<DataStorageReadOnlyException>(() => exporter.ExportTradesCsvAsync(NoTrades, "trades.csv"));
            Assert.Equal(BacktestReportExporter.DefaultFolderExportOperation, refused.Operation);

            await exporter.ExportTradesCsvAsync(NoTrades, picked, "trades.csv");

            Assert.True(File.Exists(Path.Combine(picked, "trades.csv")));
        }
        finally
        {
            try { Directory.Delete(picked, true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task ReportExporter_NoState_KeepsTheUserPickedFolderExport()
    {
        var exporter = new BacktestReportExporter();
        var picked = Path.Combine(Path.GetTempPath(), "sa_ro_export_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(picked);
        try
        {
            await exporter.ExportTradesCsvAsync(NoTrades, picked, "trades.csv");

            Assert.True(File.Exists(Path.Combine(picked, "trades.csv")));
        }
        finally
        {
            try { Directory.Delete(picked, true); } catch { /* best-effort cleanup */ }
        }
    }
}
