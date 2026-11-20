using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Discussion;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.McpServer.Limits;
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
        Assert.Equal(string.Empty, settings.Toolset);
        Assert.False(settings.UseTextOnlyResponse);
        Assert.False(settings.CompactDefaults);
        Assert.Equal(McpResponseTokenLimits.Default, settings.MaxResponseTokens);
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
            Toolset = "core,screener,backtest",
            UseTextOnlyResponse = true,
            CompactDefaults = true,
            MaxResponseTokens = McpResponseTokenLimits.RecommendedStrict,
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
        Assert.Equal("core,screener,backtest", reloaded.Toolset);
        Assert.True(reloaded.UseTextOnlyResponse);
        Assert.True(reloaded.CompactDefaults);
        Assert.Equal(McpResponseTokenLimits.RecommendedStrict, reloaded.MaxResponseTokens);
        Assert.Equal(8, reloaded.Discussion.MaxActiveSessions);
        Assert.Equal(10, reloaded.Discussion.MaxParticipantsPerSession);
        Assert.Equal(500, reloaded.Discussion.MaxMessagesPerSession);
        Assert.Equal(15, reloaded.Discussion.MaxWaitSeconds);
        Assert.True(reloaded.Discussion.CommandLogToFile);

        var config = new ConfigurationBuilder()
            .AddJsonFile(_testFilePath, optional: false)
            .Build();
        McpLimits boundLimits = config.GetSection(McpLimits.SectionName).Get<McpLimits>()!;
        Assert.Equal(McpResponseTokenLimits.RecommendedStrict, boundLimits.MaxResponseTokens);
        McpLimitsContract.Validate(boundLimits);
    }

    [Fact]
    public async Task LoadAsync_LegacyFileWithoutTokenOptimizationKeys_UsesSafeDefaults()
    {
        await File.WriteAllTextAsync(_testFilePath, """
            {
              "McpServer": {
                "EnableDiscussion": true,
                "Toolset": "core"
              },
              "AiIntegration": {
                "Discussion": {
                  "MaxActiveSessions": 6
                }
              }
            }
            """);

        var manager = new McpServerSettingsManager(_testFilePath);
        var loaded = await manager.LoadAsync();

        Assert.True(loaded.EnableDiscussion);
        Assert.Equal("core", loaded.Toolset);
        Assert.False(loaded.UseTextOnlyResponse);
        Assert.False(loaded.CompactDefaults);
        Assert.Equal(McpResponseTokenLimits.Default, loaded.MaxResponseTokens);
        Assert.Equal(6, loaded.Discussion.MaxActiveSessions);
    }

    [Fact]
    public async Task LoadAsync_WhenFileCorrupt_FallsBackToDefaultsWithoutThrowing()
    {
        await File.WriteAllTextAsync(_testFilePath, "{ broken json content !! ");

        var manager = new McpServerSettingsManager(_testFilePath);
        var settings = await manager.LoadAsync();

        Assert.NotNull(settings);
        Assert.False(settings.EnableDiscussion);
        Assert.Equal(string.Empty, settings.Toolset);
        Assert.False(settings.UseTextOnlyResponse);
        Assert.False(settings.CompactDefaults);
        Assert.Equal(McpResponseTokenLimits.Default, settings.MaxResponseTokens);
        Assert.Equal(4, settings.Discussion.MaxActiveSessions);
    }

    [Fact]
    public void McpServerSettings_Clone_ProducesIndependentCopy()
    {
        var original = new McpServerSettings
        {
            EnableDiscussion = true,
            Toolset = "core,technical",
            UseTextOnlyResponse = true,
            CompactDefaults = true,
            MaxResponseTokens = McpResponseTokenLimits.RecommendedStrict,
            Discussion = new DiscussionSettings
            {
                MaxActiveSessions = 7,
                MaxWaitSeconds = 25
            }
        };

        var clone = original.Clone();
        clone.EnableDiscussion = false;
        clone.Toolset = "ml";
        clone.UseTextOnlyResponse = false;
        clone.CompactDefaults = false;
        clone.MaxResponseTokens = McpResponseTokenLimits.Minimum;
        clone.Discussion.MaxActiveSessions = 2;

        Assert.True(original.EnableDiscussion);
        Assert.Equal("core,technical", original.Toolset);
        Assert.True(original.UseTextOnlyResponse);
        Assert.True(original.CompactDefaults);
        Assert.Equal(McpResponseTokenLimits.RecommendedStrict, original.MaxResponseTokens);
        Assert.Equal(7, original.Discussion.MaxActiveSessions);
        Assert.False(clone.EnableDiscussion);
        Assert.Equal("ml", clone.Toolset);
        Assert.False(clone.UseTextOnlyResponse);
        Assert.False(clone.CompactDefaults);
        Assert.Equal(McpResponseTokenLimits.Minimum, clone.MaxResponseTokens);
        Assert.Equal(2, clone.Discussion.MaxActiveSessions);
    }

    [Theory]
    [InlineData(false, "", false, false, "")]
    [InlineData(true, "", false, false, "--enable-discussion")]
    [InlineData(false, "core,backtest", false, false, "--toolset core,backtest")]
    [InlineData(false, "", true, false, "--response-format text-only")]
    [InlineData(false, "", false, true, "--compact-defaults")]
    [InlineData(true, " core,screener,core ", true, true, "--enable-discussion --toolset core,screener --response-format text-only --compact-defaults")]
    public void BuildLaunchArguments_CombinesDiscussionToolsetResponseFormatAndCompactDefaults(bool enableDiscussion, string toolset, bool useTextOnly, bool compactDefaults, string expectedArgs)
    {
        var settings = new McpServerSettings
        {
            EnableDiscussion = enableDiscussion,
            Toolset = toolset,
            UseTextOnlyResponse = useTextOnly,
            CompactDefaults = compactDefaults
        };

        Assert.Equal(expectedArgs, settings.BuildLaunchArguments());
    }
}
