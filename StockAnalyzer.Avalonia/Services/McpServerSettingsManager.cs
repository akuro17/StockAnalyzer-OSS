using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Discussion;
using StockAnalyzer.Core.Models.Settings;

namespace StockAnalyzer.Avalonia.Services;

/// <summary>
/// Manages user settings for StockAnalyzer.McpServer launch options and discussion parameters.
/// Persists to Data/Config/user_mcpserver_settings.json using AtomicJsonFile and SerializedPathGate.
/// </summary>
public class McpServerSettingsManager : IMcpServerSettingsManager
{
    private readonly string _settingsFilePath;
    private readonly ILogger<McpServerSettingsManager> _logger;
    private McpServerSettings _currentSettings;

    public event PropertyChangedEventHandler? PropertyChanged;

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public McpServerSettings CurrentSettings
    {
        get => _currentSettings;
        private set
        {
            if (!ReferenceEquals(_currentSettings, value))
            {
                _currentSettings = value;
                OnPropertyChanged();
            }
        }
    }

    public McpServerSettingsManager(
        string? settingsFilePath = null,
        ILogger<McpServerSettingsManager>? logger = null)
    {
        _settingsFilePath = settingsFilePath ?? PathDiscovery.ResolveConfigPath(McpServerSettings.UserConfigFileName);
        _logger = logger ?? NullLogger<McpServerSettingsManager>.Instance;
        _currentSettings = new McpServerSettings();
    }

    public async Task<McpServerSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(_settingsFilePath))
            {
                _logger.LogDebug("McpServer user settings not found at {Path}; using defaults.", _settingsFilePath);
                CurrentSettings = new McpServerSettings();
                return CurrentSettings.Clone();
            }

            using (await SerializedPathGate.AcquireAsync(_settingsFilePath, cancellationToken).ConfigureAwait(false))
            {
                var dto = await AtomicJsonFile.LoadAsync<UserMcpServerConfigDto?>(_settingsFilePath, s_jsonOptions).ConfigureAwait(false);
                if (dto is null)
                {
                    CurrentSettings = new McpServerSettings();
                }
                else
                {
                    CurrentSettings = new McpServerSettings
                    {
                        EnableDiscussion = dto.McpServer?.EnableDiscussion ?? false,
                        Discussion = dto.AiIntegration?.Discussion is not null
                            ? McpServerSettings.CloneDiscussion(dto.AiIntegration.Discussion)
                            : new DiscussionSettings()
                    };
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load McpServer settings from {Path}; reverting to defaults.", _settingsFilePath);
            CurrentSettings = new McpServerSettings();
        }

        return CurrentSettings.Clone();
    }

    public async Task SaveAsync(McpServerSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        try
        {
            var dto = new UserMcpServerConfigDto
            {
                McpServer = new McpServerSectionDto
                {
                    EnableDiscussion = settings.EnableDiscussion
                },
                AiIntegration = new AiIntegrationSectionDto
                {
                    Discussion = McpServerSettings.CloneDiscussion(settings.Discussion)
                }
            };

            using (await SerializedPathGate.AcquireAsync(_settingsFilePath, cancellationToken).ConfigureAwait(false))
            {
                await AtomicJsonFile.SaveAsync(_settingsFilePath, dto, s_jsonOptions).ConfigureAwait(false);
            }

            CurrentSettings = settings.Clone();
            _logger.LogInformation("McpServer settings saved successfully to {Path}.", _settingsFilePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save McpServer settings to {Path}.", _settingsFilePath);
            throw;
        }
    }

    public void ResetToDefault()
    {
        CurrentSettings = new McpServerSettings();
    }

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    internal sealed class UserMcpServerConfigDto
    {
        public McpServerSectionDto McpServer { get; set; } = new();
        public AiIntegrationSectionDto AiIntegration { get; set; } = new();
    }

    internal sealed class McpServerSectionDto
    {
        public bool EnableDiscussion { get; set; }
    }

    internal sealed class AiIntegrationSectionDto
    {
        public DiscussionSettings Discussion { get; set; } = new();
    }
}
