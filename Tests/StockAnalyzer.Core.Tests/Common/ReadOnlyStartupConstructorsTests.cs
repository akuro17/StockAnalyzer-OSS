using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Notes;
using StockAnalyzer.TestSupport;
using Xunit;

namespace StockAnalyzer.Core.Tests.Common;

/// <summary>
/// D-2' Run 4 / S5: the three constructors that stopped the whole application from starting in a read-only install now come up in their
/// read-only form (no folder created, nothing swallowed), while with no state or a writable one they behave exactly as before.
/// These tests swap the process-wide base directory, so they run alone.
/// </summary>
[Collection("PathDiscovery UIA environment")]
public class ReadOnlyStartupConstructorsTests
{
    // ---- notes database -----------------------------------------------------------------------------------------

    [Fact]
    public async Task NoteDatabase_ReadOnly_IsUnavailable_CreatesNoFolder_AndRefusesToOpen()
    {
        using var fixture = ReadOnlyDataRootFixture.Create();
        using (fixture.UseAsBaseDirectory())
        {
            var manager = new NoteDatabaseConnectionManager(dataStorageState: TestDataStorageState.ReadOnlyState());
            var attachments = new AttachmentRepository(manager);

            Assert.False(manager.IsAvailable);
            await Assert.ThrowsAsync<DataStorageReadOnlyException>(() => manager.OpenConnectionAsync());
            Assert.NotNull(attachments);
            Assert.Empty(Directory.GetFileSystemEntries(fixture.Root));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NoteDatabase_NoStateOrWritable_CreatesTheFoldersAsBefore(bool withWritableState)
    {
        using var fixture = ReadOnlyDataRootFixture.Create(readOnly: false);
        using (fixture.UseAsBaseDirectory())
        {
            var manager = new NoteDatabaseConnectionManager(dataStorageState: withWritableState ? TestDataStorageState.WritableState() : null);
            _ = new AttachmentRepository(manager);

            Assert.True(manager.IsAvailable);
            Assert.True(Directory.Exists(Path.Combine(fixture.DataRoot, "Notes", "Attachments")));
        }
    }

    [Fact]
    public void NoteDatabase_WithAnExplicitDirectoryAndNoState_IsUnchanged()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sa_notes_ctor_" + Guid.NewGuid().ToString("N"));
        try
        {
            var manager = new NoteDatabaseConnectionManager(notesDirectoryOverride: directory);

            Assert.True(manager.IsAvailable);
            Assert.True(Directory.Exists(directory));
            Assert.Equal(Path.Combine(directory, "notes.db"), manager.DatabasePath);
        }
        finally
        {
            try { Directory.Delete(directory, true); } catch { /* best-effort cleanup */ }
        }
    }

    // ---- market data provider -----------------------------------------------------------------------------------

    private static ParquetMarketDataProvider NewProvider(IDataStorageState? state) =>
        new(new DuckDBConnectionManager(), UnavailablePythonService.Instance, Options.Create(new MarketDataSettings()), dataStorageState: state);

    [Fact]
    public async Task MarketData_ReadOnly_StartsWithoutCreatingFolders_AndReadsAsNoData()
    {
        using var fixture = ReadOnlyDataRootFixture.Create();
        using (fixture.UseAsBaseDirectory())
        {
            var provider = NewProvider(TestDataStorageState.ReadOnlyState());

            Assert.Empty(await provider.GetAvailableTickersAsync());
            Assert.Empty(Directory.GetFileSystemEntries(fixture.Root));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MarketData_NoStateOrWritable_CreatesTheDataFoldersAsBefore(bool withWritableState)
    {
        using var fixture = ReadOnlyDataRootFixture.Create(readOnly: false);
        using (fixture.UseAsBaseDirectory())
        {
            _ = NewProvider(withWritableState ? TestDataStorageState.WritableState() : null);

            foreach (var folder in new[] { "Daily", "Weekly", "Monthly", "Metadata" })
            {
                Assert.True(Directory.Exists(Path.Combine(fixture.DataRoot, folder)), folder);
            }
        }
    }
}
