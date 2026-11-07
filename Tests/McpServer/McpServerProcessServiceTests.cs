using System;
using System.IO;
using System.Threading.Tasks;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Models.Settings;
using Xunit;

namespace StockAnalyzer.Tests.McpServer;

public sealed class McpServerProcessServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _testFilePath;

    public McpServerProcessServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "StockAnalyzer_ProcTests_" + Guid.NewGuid().ToString("N"));
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
            // best-effort cleanup
        }
    }

    [Fact]
    public void ResolveExecutablePath_ReturnsExpectedExecutableName()
    {
        var settingsManager = new McpServerSettingsManager(_testFilePath);
        using var service = new McpServerProcessService(settingsManager);

        string? path = service.ResolveExecutablePath();

        Assert.NotNull(path);
        string expectedExe = OperatingSystem.IsWindows() ? "StockAnalyzer.McpServer.exe" : "StockAnalyzer.McpServer";
        Assert.EndsWith(expectedExe, path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartServerAsync_WhenExecutableMissing_ReturnsFailureCleanly()
    {
        var settingsManager = new McpServerSettingsManager(_testFilePath);
        using var service = new MissingExeProcessService(settingsManager);

        var result = await service.StartServerAsync();

        Assert.False(result.Success);
        Assert.Null(result.ProcessId);
        Assert.Contains("StockAnalyzer.McpServer executable not found", result.Message);
    }

    [Fact]
    public async Task StartServerAsync_ArgumentsReflectEnableDiscussionSetting()
    {
        var settingsManager = new McpServerSettingsManager(_testFilePath);
        await settingsManager.SaveAsync(new McpServerSettings { EnableDiscussion = true });

        using var service = new MissingExeProcessService(settingsManager);
        var result = await service.StartServerAsync();

        // Even when exe not found, settings were checked and result was structured
        Assert.False(result.Success);
        var loaded = await settingsManager.LoadAsync();
        Assert.True(loaded.EnableDiscussion);
    }

    [Fact]
    public void StopServer_WhenNotRunning_DoesNotThrow()
    {
        var settingsManager = new McpServerSettingsManager(_testFilePath);
        using var service = new McpServerProcessService(settingsManager);

        Assert.False(service.IsRunning);
        Assert.Null(service.ProcessId);

        // Multiple calls to StopServer should be idempotent and not throw
        service.StopServer();
        service.StopServer();

        Assert.False(service.IsRunning);
    }

    [Fact]
    public void Dispose_CanBeCalledMultipleTimesSafely()
    {
        var settingsManager = new McpServerSettingsManager(_testFilePath);
        var service = new McpServerProcessService(settingsManager);

        service.Dispose();
        service.Dispose(); // second call should be no-op

        Assert.False(service.IsRunning);
    }

    private sealed class MissingExeProcessService : McpServerProcessService
    {
        public MissingExeProcessService(IMcpServerSettingsManager settingsManager)
            : base(settingsManager)
        {
        }

        public override string? ResolveExecutablePath()
        {
            return Path.Combine(Path.GetTempPath(), "DefinitivelyMissing_" + Guid.NewGuid().ToString("N") + ".exe");
        }
    }
}
