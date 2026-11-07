using System;
using System.IO;
using System.Threading.Tasks;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Discussion;
using StockAnalyzer.Core.Models.Settings;
using Xunit;

namespace StockAnalyzer.Tests.McpServer;

public sealed class McpServerSettingsManagerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _testFilePath;

    public McpServerSettingsManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "StockAnalyzer_Tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _testFilePath = Path.Combine(_tempDir, "user_mcpserver_settings.json");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // best-effort test cleanup
        }
    }

    [Fact]
    public async Task LoadAsync_WhenFileDoesNotExist_ReturnsDefaultSettings()
    {
        var manager = new McpServerSettingsManager(_testFilePath);
        var settings = await manager.LoadAsync();

        Assert.NotNull(settings);
        Assert.False(settings.EnableDiscussion);
        Assert.Equal(4, settings.Discussion.MaxActiveSessions);
        Assert.Equal(6, settings.Discussion.MaxParticipantsPerSession);
        Assert.Equal(300, settings.Discussion.MaxMessagesPerSession);
        Assert.Equal(20, settings.Discussion.MaxWaitSeconds);
    }

    [Fact]
    public async Task SaveAsync_And_LoadAsync_PersistsAndRestoresValues()
    {
        var manager = new McpServerSettingsManager(_testFilePath);
        var original = new McpServerSettings
        {
            EnableDiscussion = true,
            Discussion = new DiscussionSettings
            {
                MaxActiveSessions = 8,
                MaxParticipantsPerSession = 10,
                MaxMessagesPerSession = 500,
                MaxWaitSeconds = 15,
                CommandLogToFile = true
            }
        };

        await manager.SaveAsync(original);

        Assert.True(File.Exists(_testFilePath));

        var reloadedManager = new McpServerSettingsManager(_testFilePath);
        var reloaded = await reloadedManager.LoadAsync();

        Assert.True(reloaded.EnableDiscussion);
        Assert.Equal(8, reloaded.Discussion.MaxActiveSessions);
        Assert.Equal(10, reloaded.Discussion.MaxParticipantsPerSession);
        Assert.Equal(500, reloaded.Discussion.MaxMessagesPerSession);
        Assert.Equal(15, reloaded.Discussion.MaxWaitSeconds);
        Assert.True(reloaded.Discussion.CommandLogToFile);
    }

    [Fact]
    public async Task LoadAsync_WhenFileCorrupt_FallsBackToDefaultsWithoutThrowing()
    {
        await File.WriteAllTextAsync(_testFilePath, "{ broken json content !! ");

        var manager = new McpServerSettingsManager(_testFilePath);
        var settings = await manager.LoadAsync();

        Assert.NotNull(settings);
        Assert.False(settings.EnableDiscussion);
        Assert.Equal(4, settings.Discussion.MaxActiveSessions);
    }

    [Fact]
    public void McpServerSettings_Clone_ProducesIndependentCopy()
    {
        var original = new McpServerSettings
        {
            EnableDiscussion = true,
            Discussion = new DiscussionSettings
            {
                MaxActiveSessions = 7,
                MaxWaitSeconds = 25
            }
        };

        var clone = original.Clone();
        clone.EnableDiscussion = false;
        clone.Discussion.MaxActiveSessions = 2;

        Assert.True(original.EnableDiscussion);
        Assert.Equal(7, original.Discussion.MaxActiveSessions);
        Assert.False(clone.EnableDiscussion);
        Assert.Equal(2, clone.Discussion.MaxActiveSessions);
    }
}
