using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Models.Watchlist;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;
using StockAnalyzer.TestSupport;
using Xunit;

namespace StockAnalyzer.Core.Tests.ReadOnly;

public sealed class ReadOnlyWorkspaceSourceTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly string _workspacePath;

    public ReadOnlyWorkspaceSourceTests()
    {
        _tempDirectory = TestDataIsolationBootstrap.NewTempDirectory("ReadOnlyWorkspaceTests");
        Directory.CreateDirectory(_tempDirectory);
        _workspacePath = Path.Combine(_tempDirectory, "workspace.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            try { Directory.Delete(_tempDirectory, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task LoadAsync_WhenFileDoesNotExist_ReturnsNull()
    {
        var source = new FileReadOnlyWorkspaceSource(_workspacePath);
        var settings = await source.LoadAsync();
        Assert.Null(settings);
    }

    [Fact]
    public async Task LoadAsync_WhenFileIsCorrupt_ThrowsDataUnavailableException()
    {
        await File.WriteAllTextAsync(_workspacePath, "{ this is not valid json }");
        var source = new FileReadOnlyWorkspaceSource(_workspacePath);

        var ex = await Assert.ThrowsAsync<DataUnavailableException>(() => source.LoadAsync());
        Assert.Equal("The workspace file could not be read.", ex.Message);
    }

    [Fact]
    public async Task LoadAsync_WhenFileIsValid_ReturnsWorkspaceSettings()
    {
        var original = new WorkspaceSettings
        {
            Name = "Test Workspace",
            WatchlistProfiles = new List<WatchlistProfile>
            {
                new(Guid.NewGuid(), "Tech", IndicatorColor.FromRgb(0, 0, 255), isPortfolio: false)
            }
        };

        var serializer = new WorkspaceSerializationService();
        await serializer.SaveAsync(original, _workspacePath);

        var source = new FileReadOnlyWorkspaceSource(_workspacePath);
        var loaded = await source.LoadAsync();

        Assert.NotNull(loaded);
        Assert.Equal("Test Workspace", loaded.Name);
        Assert.Single(loaded.WatchlistProfiles);
        Assert.Equal("Tech", loaded.WatchlistProfiles[0].Name);
    }

    [Fact]
    public async Task LoadAsync_PermitsConcurrentFileReplaceDueToProducerSharedReadShare()
    {
        var original = new WorkspaceSettings { Name = "V1" };
        var serializer = new WorkspaceSerializationService();
        await serializer.SaveAsync(original, _workspacePath);

        string tempWritePath = Path.Combine(_tempDirectory, "workspace.tmp");
        var updated = new WorkspaceSettings { Name = "V2" };
        await serializer.SaveAsync(updated, tempWritePath);

        // Open with FileReadOnlyWorkspaceSource's stream share: ReadWrite | Delete
        await using (var readerStream = new FileStream(
            _workspacePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true))
        {
            // Concurrently replace the file while the reader holds the handle open
            File.Replace(tempWritePath, _workspacePath, null);
        }

        var source = new FileReadOnlyWorkspaceSource(_workspacePath);
        var reloaded = await source.LoadAsync();

        Assert.NotNull(reloaded);
        Assert.Equal("V2", reloaded.Name);
    }

    [Fact]
    public async Task LoadAsync_WhenTransientShareViolationOccurs_RetriesAndSucceeds()
    {
        var original = new WorkspaceSettings { Name = "RetrySuccess" };
        var serializer = new WorkspaceSerializationService();
        await serializer.SaveAsync(original, _workspacePath);

        // Hold an exclusive lock on the file for ~20ms on a background task while calling LoadAsync
        var lockTask = Task.Run(async () =>
        {
            using var exclusiveStream = new FileStream(_workspacePath, FileMode.Open, FileAccess.Read, FileShare.None);
            await Task.Delay(20);
        });

        // Small initial delay so exclusiveStream is opened before LoadAsync's first attempt
        await Task.Delay(5);

        var source = new FileReadOnlyWorkspaceSource(_workspacePath);
        var loaded = await source.LoadAsync();

        await lockTask;

        Assert.NotNull(loaded);
        Assert.Equal("RetrySuccess", loaded.Name);
    }
}
