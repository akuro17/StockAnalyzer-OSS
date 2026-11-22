using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Core.Discussion;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Avalonia.Services;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs;

/// <summary>
/// ViewModel for Settings > McpServer.
/// Manages user configuration of StockAnalyzer.McpServer launch options (--enable-discussion, --toolset),
/// runtime server process controls (Stop / Restart), and the 22 tunable discussion hub parameters.
/// </summary>
public partial class McpServerSettingsViewModel : ViewModelBase, ISettingsPageViewModel, IDisposable
{
    private readonly IMcpServerSettingsManager _manager;
    private readonly ILogger<McpServerSettingsViewModel> _logger;
    private readonly IDispatcherService? _dispatcherService;
    private readonly IMcpServerProcessService? _processService;
    private readonly SynchronizationContext? _syncContext;
    private McpServerSettings _snapshot;
    private bool _isApplying;
    private bool _isSyncingToolset;
    private bool _isDisposed;

    public string TitleKey => "Settings_McpServer";
    public string IconKey => "SettingsBacktestIcon";

    // Group A: Launch options & Server controls
    [ObservableProperty] private bool _enableDiscussion;
    [ObservableProperty] private bool _useTextOnlyResponse;
    [ObservableProperty] private bool _compactDefaults;
    [ObservableProperty] private bool _progressiveDiscovery;
    [ObservableProperty] private int _maxResponseTokens = McpResponseTokenLimits.Default;
    [ObservableProperty] private string _toolset = string.Empty;
    [ObservableProperty] private bool _useAllToolsets = true;
    [ObservableProperty] private bool _useCoreToolset;
    [ObservableProperty] private bool _useTechnicalToolset;
    [ObservableProperty] private bool _useTechnicalExtendedToolset;
    [ObservableProperty] private bool _useScreenerToolset;
    [ObservableProperty] private bool _useBacktestToolset;
    [ObservableProperty] private bool _usePortfolioToolset;
    [ObservableProperty] private bool _useMlToolset;

    public int MaxResponseTokensMinimum => McpResponseTokenLimits.Minimum;
    public int MaxResponseTokensMaximum => McpResponseTokenLimits.Maximum;
    public int MaxResponseTokensIncrement => McpResponseTokenLimits.Kibibyte;
    public int MaxResponseTokensRecommended => McpResponseTokenLimits.RecommendedStrict;

    [RelayCommand]
    private void ApplyRecommendedMaxResponseTokens() => MaxResponseTokens = McpResponseTokenLimits.RecommendedStrict;

    [RelayCommand]
    private void ApplyProgressiveTokenPreset()
    {
        ProgressiveDiscovery = true;
        Toolset = string.Empty;
        UseTextOnlyResponse = true;
        CompactDefaults = true;
        MaxResponseTokens = McpResponseTokenLimits.RecommendedStrict;
    }

    [RelayCommand]
    private void ApplyRecommendedTokenPreset()
    {
        Toolset = McpToolsetProfiles.RecommendedPresetToolset;
        UseTextOnlyResponse = true;
        CompactDefaults = true;
        MaxResponseTokens = McpResponseTokenLimits.RecommendedStrict;
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StopServerCommand))]
    [NotifyCanExecuteChangedFor(nameof(RestartServerCommand))]
    private bool _isServerOperationInProgress;

    [ObservableProperty]
    private string _serverActionMessage = string.Empty;

    public bool IsServerRunning => _processService?.IsRunning ?? false;
    public int? ServerProcessId => _processService?.ProcessId;
    public string ServerStatusText => IsServerRunning && ServerProcessId is int pid
        ? string.Format(CultureInfo.InvariantCulture, Localize("McpServer_Status_Running", "Status: Running (PID: {0})"), pid)
        : Localize("McpServer_Status_Stopped", "Status: Stopped (not running under GUI)");

    private static string Localize(string key, string fallback)
    {
        string value = LocalizationManager.Instance[key];
        return string.IsNullOrEmpty(value) || string.Equals(value, $"[{key}]", StringComparison.Ordinal)
            ? fallback
            : value;
    }

    public string LaunchArgumentsPreview => BuildSettings().BuildLaunchArguments();
    public bool HasLaunchArguments => !string.IsNullOrEmpty(LaunchArgumentsPreview);
    public string LaunchArgumentsDisplay => HasLaunchArguments
        ? string.Format(CultureInfo.InvariantCulture, Localize("McpServer_Label_LaunchArgs", "CLI Args: {0}"), LaunchArgumentsPreview)
        : string.Empty;

    // Group B: Session & Participant limits
    [ObservableProperty] private int _maxActiveSessions = 4;
    [ObservableProperty] private int _maxParticipantsPerSession = 6;
    [ObservableProperty] private int _maxMessagesPerSession = 300;
    [ObservableProperty] private int _maxContextsPerSession = 20;

    // Group C: Payload & Buffer limits
    [ObservableProperty] private int _maxMessageBytes = 16 * 1024;
    [ObservableProperty] private int _maxContextBytes = 512 * 1024;
    [ObservableProperty] private long _maxTotalBytes = 64L * 1024 * 1024;
    [ObservableProperty] private int _maxPostsPerMinute = 20;

    // Group D: Timeouts & Read limits
    [ObservableProperty] private int _idleTimeoutSeconds = 1800;
    [ObservableProperty] private int _maxSessionAgeSeconds = 7200;
    [ObservableProperty] private int _closedRetentionSeconds = 300;
    [ObservableProperty] private int _awaySeconds = 120;
    [ObservableProperty] private int _floorTimeoutSeconds = 60;
    [ObservableProperty] private int _maxWaitSeconds = 20;
    [ObservableProperty] private int _defaultReadLimit = 20;
    [ObservableProperty] private int _maxReadLimit = 50;

    // Group E: Audit & Command logging
    [ObservableProperty] private int _auditCapacity = 1000;
    [ObservableProperty] private int _commandLogCapacity = 1000;
    [ObservableProperty] private bool _commandLogToFile;
    [ObservableProperty] private int _commandLogMaxFileBytes = 2 * 1024 * 1024;
    [ObservableProperty] private int _commandLogGenerations = 3;
    [ObservableProperty] private int _commandLogMaxAgeHours = 24;

    // Error messages dictionary
    private readonly Dictionary<string, string> _errors = new(StringComparer.Ordinal);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasValidationMessage))]
    private string _validationMessage = string.Empty;

    public bool HasValidationMessage => !string.IsNullOrEmpty(ValidationMessage);

    private string ErrorOf(string propertyName) => _errors.TryGetValue(propertyName, out string? err) ? err : string.Empty;

    // Per-field validation error properties for UI bindings
    public string ToolsetError => ErrorOf(nameof(Toolset));
    public string MaxResponseTokensError => ErrorOf(nameof(MaxResponseTokens));
    public string MaxActiveSessionsError => ErrorOf(nameof(MaxActiveSessions));
    public string MaxParticipantsPerSessionError => ErrorOf(nameof(MaxParticipantsPerSession));
    public string MaxMessagesPerSessionError => ErrorOf(nameof(MaxMessagesPerSession));
    public string MaxContextsPerSessionError => ErrorOf(nameof(MaxContextsPerSession));
    public string MaxMessageBytesError => ErrorOf(nameof(MaxMessageBytes));
    public string MaxContextBytesError => ErrorOf(nameof(MaxContextBytes));
    public string MaxTotalBytesError => ErrorOf(nameof(MaxTotalBytes));
    public string MaxPostsPerMinuteError => ErrorOf(nameof(MaxPostsPerMinute));
    public string IdleTimeoutSecondsError => ErrorOf(nameof(IdleTimeoutSeconds));
    public string MaxSessionAgeSecondsError => ErrorOf(nameof(MaxSessionAgeSeconds));
    public string ClosedRetentionSecondsError => ErrorOf(nameof(ClosedRetentionSeconds));
    public string AwaySecondsError => ErrorOf(nameof(AwaySeconds));
    public string FloorTimeoutSecondsError => ErrorOf(nameof(FloorTimeoutSeconds));
    public string MaxWaitSecondsError => ErrorOf(nameof(MaxWaitSeconds));
    public string DefaultReadLimitError => ErrorOf(nameof(DefaultReadLimit));
    public string MaxReadLimitError => ErrorOf(nameof(MaxReadLimit));
    public string AuditCapacityError => ErrorOf(nameof(AuditCapacity));
    public string CommandLogCapacityError => ErrorOf(nameof(CommandLogCapacity));
    public string CommandLogMaxFileBytesError => ErrorOf(nameof(CommandLogMaxFileBytes));
    public string CommandLogGenerationsError => ErrorOf(nameof(CommandLogGenerations));
    public string CommandLogMaxAgeHoursError => ErrorOf(nameof(CommandLogMaxAgeHours));

    public bool IsValid => _errors.Count == 0;

    public bool IsModified
    {
        get
        {
            var current = BuildSettings();
            return current.EnableDiscussion != _snapshot.EnableDiscussion
                || current.UseTextOnlyResponse != _snapshot.UseTextOnlyResponse
                || current.CompactDefaults != _snapshot.CompactDefaults
                || current.ProgressiveDiscovery != _snapshot.ProgressiveDiscovery
                || current.MaxResponseTokens != _snapshot.MaxResponseTokens
                || !string.Equals(current.Toolset, _snapshot.Toolset, StringComparison.Ordinal)
                || current.Discussion.MaxActiveSessions != _snapshot.Discussion.MaxActiveSessions
                || current.Discussion.MaxParticipantsPerSession != _snapshot.Discussion.MaxParticipantsPerSession
                || current.Discussion.MaxMessagesPerSession != _snapshot.Discussion.MaxMessagesPerSession
                || current.Discussion.MaxContextsPerSession != _snapshot.Discussion.MaxContextsPerSession
                || current.Discussion.MaxMessageBytes != _snapshot.Discussion.MaxMessageBytes
                || current.Discussion.MaxContextBytes != _snapshot.Discussion.MaxContextBytes
                || current.Discussion.MaxTotalBytes != _snapshot.Discussion.MaxTotalBytes
                || current.Discussion.MaxPostsPerMinute != _snapshot.Discussion.MaxPostsPerMinute
                || current.Discussion.IdleTimeoutSeconds != _snapshot.Discussion.IdleTimeoutSeconds
                || current.Discussion.MaxSessionAgeSeconds != _snapshot.Discussion.MaxSessionAgeSeconds
                || current.Discussion.ClosedRetentionSeconds != _snapshot.Discussion.ClosedRetentionSeconds
                || current.Discussion.AwaySeconds != _snapshot.Discussion.AwaySeconds
                || current.Discussion.FloorTimeoutSeconds != _snapshot.Discussion.FloorTimeoutSeconds
                || current.Discussion.MaxWaitSeconds != _snapshot.Discussion.MaxWaitSeconds
                || current.Discussion.DefaultReadLimit != _snapshot.Discussion.DefaultReadLimit
                || current.Discussion.MaxReadLimit != _snapshot.Discussion.MaxReadLimit
                || current.Discussion.AuditCapacity != _snapshot.Discussion.AuditCapacity
                || current.Discussion.CommandLogCapacity != _snapshot.Discussion.CommandLogCapacity
                || current.Discussion.CommandLogToFile != _snapshot.Discussion.CommandLogToFile
                || current.Discussion.CommandLogMaxFileBytes != _snapshot.Discussion.CommandLogMaxFileBytes
                || current.Discussion.CommandLogGenerations != _snapshot.Discussion.CommandLogGenerations
                || current.Discussion.CommandLogMaxAgeHours != _snapshot.Discussion.CommandLogMaxAgeHours;
        }
    }

    public Task LoadTask { get; }

    public McpServerSettingsViewModel(
        IMcpServerSettingsManager manager,
        ILogger<McpServerSettingsViewModel>? logger = null,
        IDispatcherService? dispatcherService = null,
        IMcpServerProcessService? processService = null)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _logger = logger ?? NullLogger<McpServerSettingsViewModel>.Instance;
        _dispatcherService = dispatcherService;
        _processService = processService;
        _syncContext = SynchronizationContext.Current;
        _snapshot = new McpServerSettings();

        if (_processService is not null)
        {
            _processService.PropertyChanged += OnProcessServicePropertyChanged;
        }

        LoadTask = LoadAsync();
    }

    private void OnProcessServicePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IMcpServerProcessService.IsRunning) or nameof(IMcpServerProcessService.ProcessId) or null)
        {
            DispatchOnUI(NotifyServerStateChanged);
        }
    }

    private void NotifyServerStateChanged()
    {
        OnPropertyChanged(nameof(IsServerRunning));
        OnPropertyChanged(nameof(ServerProcessId));
        OnPropertyChanged(nameof(ServerStatusText));
        StopServerCommand.NotifyCanExecuteChanged();
        RestartServerCommand.NotifyCanExecuteChanged();
    }

    private async Task LoadAsync()
    {
        try
        {
            var loaded = await _manager.LoadAsync().ConfigureAwait(false);
            _snapshot = loaded.Clone();
            DispatchOnUI(() => ApplySettings(_snapshot));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load McpServer settings in ViewModel.");
            _snapshot = new McpServerSettings();
            DispatchOnUI(() => ApplySettings(_snapshot));
        }
    }

    private void DispatchOnUI(Action action)
    {
        if (_dispatcherService is not null)
        {
            _dispatcherService.Post(action);
        }
        else if (_syncContext is not null && SynchronizationContext.Current != _syncContext)
        {
            _syncContext.Post(_ => action(), null);
        }
        else
        {
            action();
        }
    }

    private void ApplySettings(McpServerSettings s)
    {
        _isApplying = true;
        try
        {
            EnableDiscussion = s.EnableDiscussion;
            UseTextOnlyResponse = s.UseTextOnlyResponse;
            CompactDefaults = s.CompactDefaults;
            ProgressiveDiscovery = s.ProgressiveDiscovery;
            MaxResponseTokens = s.MaxResponseTokens;
            Toolset = McpToolsetProfiles.NormalizeText(s.Toolset);
            SyncCheckboxesFromToolset(Toolset);
            MaxActiveSessions = s.Discussion.MaxActiveSessions;
            MaxParticipantsPerSession = s.Discussion.MaxParticipantsPerSession;
            MaxMessagesPerSession = s.Discussion.MaxMessagesPerSession;
            MaxContextsPerSession = s.Discussion.MaxContextsPerSession;
            MaxMessageBytes = s.Discussion.MaxMessageBytes;
            MaxContextBytes = s.Discussion.MaxContextBytes;
            MaxTotalBytes = s.Discussion.MaxTotalBytes;
            MaxPostsPerMinute = s.Discussion.MaxPostsPerMinute;
            IdleTimeoutSeconds = s.Discussion.IdleTimeoutSeconds;
            MaxSessionAgeSeconds = s.Discussion.MaxSessionAgeSeconds;
            ClosedRetentionSeconds = s.Discussion.ClosedRetentionSeconds;
            AwaySeconds = s.Discussion.AwaySeconds;
            FloorTimeoutSeconds = s.Discussion.FloorTimeoutSeconds;
            MaxWaitSeconds = s.Discussion.MaxWaitSeconds;
            DefaultReadLimit = s.Discussion.DefaultReadLimit;
            MaxReadLimit = s.Discussion.MaxReadLimit;
            AuditCapacity = s.Discussion.AuditCapacity;
            CommandLogCapacity = s.Discussion.CommandLogCapacity;
            CommandLogToFile = s.Discussion.CommandLogToFile;
            CommandLogMaxFileBytes = s.Discussion.CommandLogMaxFileBytes;
            CommandLogGenerations = s.Discussion.CommandLogGenerations;
            CommandLogMaxAgeHours = s.Discussion.CommandLogMaxAgeHours;
        }
        finally
        {
            _isApplying = false;
        }

        ValidateAll();
        OnPropertyChanged(nameof(IsModified));
        NotifyLaunchArgumentsChanged();
    }

    private void NotifyLaunchArgumentsChanged()
    {
        OnPropertyChanged(nameof(LaunchArgumentsPreview));
        OnPropertyChanged(nameof(HasLaunchArguments));
        OnPropertyChanged(nameof(LaunchArgumentsDisplay));
    }

    public McpServerSettings BuildSettings()
    {
        return new McpServerSettings
        {
            EnableDiscussion = EnableDiscussion,
            UseTextOnlyResponse = UseTextOnlyResponse,
            CompactDefaults = CompactDefaults,
            ProgressiveDiscovery = ProgressiveDiscovery,
            MaxResponseTokens = MaxResponseTokens,
            Toolset = McpToolsetProfiles.NormalizeText(Toolset),
            Discussion = new DiscussionSettings
            {
                MaxActiveSessions = MaxActiveSessions,
                MaxParticipantsPerSession = MaxParticipantsPerSession,
                MaxMessagesPerSession = MaxMessagesPerSession,
                MaxContextsPerSession = MaxContextsPerSession,
                MaxMessageBytes = MaxMessageBytes,
                MaxContextBytes = MaxContextBytes,
                MaxTotalBytes = MaxTotalBytes,
                MaxPostsPerMinute = MaxPostsPerMinute,
                IdleTimeoutSeconds = IdleTimeoutSeconds,
                MaxSessionAgeSeconds = MaxSessionAgeSeconds,
                ClosedRetentionSeconds = ClosedRetentionSeconds,
                AwaySeconds = AwaySeconds,
                FloorTimeoutSeconds = FloorTimeoutSeconds,
                MaxWaitSeconds = MaxWaitSeconds,
                DefaultReadLimit = DefaultReadLimit,
                MaxReadLimit = MaxReadLimit,
                AuditCapacity = AuditCapacity,
                CommandLogCapacity = CommandLogCapacity,
                CommandLogToFile = CommandLogToFile,
                CommandLogMaxFileBytes = CommandLogMaxFileBytes,
                CommandLogGenerations = CommandLogGenerations,
                CommandLogMaxAgeHours = CommandLogMaxAgeHours
            }
        };
    }

    partial void OnToolsetChanged(string value)
    {
        if (!_isSyncingToolset)
        {
            SyncCheckboxesFromToolset(value);
        }
    }

    partial void OnUseAllToolsetsChanged(bool value)
    {
        if (_isSyncingToolset) return;
        if (value)
        {
            SetDomainFlagsAndToolset(false, false, false, false, false, false, false, string.Empty);
        }
        else if (!UseCoreToolset && !UseTechnicalToolset && !UseTechnicalExtendedToolset && !UseScreenerToolset && !UseBacktestToolset && !UsePortfolioToolset && !UseMlToolset)
        {
            _isSyncingToolset = true;
            try
            {
                UseAllToolsets = true;
            }
            finally
            {
                _isSyncingToolset = false;
            }
        }
    }

    partial void OnUseCoreToolsetChanged(bool value) => UpdateToolsetFromDomainFlags();
    partial void OnUseTechnicalToolsetChanged(bool value) => UpdateToolsetFromDomainFlags();
    partial void OnUseTechnicalExtendedToolsetChanged(bool value) => UpdateToolsetFromDomainFlags();
    partial void OnUseScreenerToolsetChanged(bool value) => UpdateToolsetFromDomainFlags();
    partial void OnUseBacktestToolsetChanged(bool value) => UpdateToolsetFromDomainFlags();
    partial void OnUsePortfolioToolsetChanged(bool value) => UpdateToolsetFromDomainFlags();
    partial void OnUseMlToolsetChanged(bool value) => UpdateToolsetFromDomainFlags();

    private void SyncCheckboxesFromToolset(string raw)
    {
        _isSyncingToolset = true;
        try
        {
            string normalized = McpToolsetProfiles.NormalizeText(raw);
            if (string.IsNullOrEmpty(normalized))
            {
                UseAllToolsets = true;
                UseCoreToolset = false;
                UseTechnicalToolset = false;
                UseTechnicalExtendedToolset = false;
                UseScreenerToolset = false;
                UseBacktestToolset = false;
                UsePortfolioToolset = false;
                UseMlToolset = false;
                return;
            }

            if (McpToolsetProfiles.TryParse(normalized, out IReadOnlyList<string> parsed))
            {
                bool hasAll = parsed.Contains(McpToolsetProfiles.All, StringComparer.Ordinal);
                UseAllToolsets = hasAll;
                UseCoreToolset = !hasAll && parsed.Contains(McpToolsetProfiles.Core, StringComparer.Ordinal);
                UseTechnicalToolset = !hasAll && parsed.Contains(McpToolsetProfiles.Technical, StringComparer.Ordinal);
                UseTechnicalExtendedToolset = !hasAll && parsed.Contains(McpToolsetProfiles.TechnicalExtended, StringComparer.Ordinal);
                UseScreenerToolset = !hasAll && parsed.Contains(McpToolsetProfiles.Screener, StringComparer.Ordinal);
                UseBacktestToolset = !hasAll && parsed.Contains(McpToolsetProfiles.Backtest, StringComparer.Ordinal);
                UsePortfolioToolset = !hasAll && parsed.Contains(McpToolsetProfiles.Portfolio, StringComparer.Ordinal);
                UseMlToolset = !hasAll && parsed.Contains(McpToolsetProfiles.Ml, StringComparer.Ordinal);
            }
        }
        finally
        {
            _isSyncingToolset = false;
        }
    }

    private void UpdateToolsetFromDomainFlags()
    {
        if (_isSyncingToolset) return;

        var selected = new List<string>(7);
        if (UseCoreToolset) selected.Add(McpToolsetProfiles.Core);
        if (UseTechnicalToolset) selected.Add(McpToolsetProfiles.Technical);
        if (UseTechnicalExtendedToolset) selected.Add(McpToolsetProfiles.TechnicalExtended);
        if (UseScreenerToolset) selected.Add(McpToolsetProfiles.Screener);
        if (UseBacktestToolset) selected.Add(McpToolsetProfiles.Backtest);
        if (UsePortfolioToolset) selected.Add(McpToolsetProfiles.Portfolio);
        if (UseMlToolset) selected.Add(McpToolsetProfiles.Ml);

        _isSyncingToolset = true;
        try
        {
            if (selected.Count == 0)
            {
                UseAllToolsets = true;
                Toolset = string.Empty;
            }
            else
            {
                UseAllToolsets = false;
                Toolset = string.Join(",", selected);
            }
        }
        finally
        {
            _isSyncingToolset = false;
        }
    }

    private void SetDomainFlagsAndToolset(bool core, bool technical, bool technicalExtended, bool screener, bool backtest, bool portfolio, bool ml, string toolset)
    {
        _isSyncingToolset = true;
        try
        {
            UseCoreToolset = core;
            UseTechnicalToolset = technical;
            UseTechnicalExtendedToolset = technicalExtended;
            UseScreenerToolset = screener;
            UseBacktestToolset = backtest;
            UsePortfolioToolset = portfolio;
            UseMlToolset = ml;
            Toolset = toolset;
        }
        finally
        {
            _isSyncingToolset = false;
        }
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        if (_isApplying) return;

        if (e.PropertyName != nameof(IsModified)
            && e.PropertyName != nameof(ValidationMessage)
            && e.PropertyName != nameof(HasValidationMessage)
            && e.PropertyName != nameof(LaunchArgumentsPreview)
            && e.PropertyName != nameof(HasLaunchArguments)
            && e.PropertyName != nameof(LaunchArgumentsDisplay)
            && e.PropertyName != nameof(IsServerRunning)
            && e.PropertyName != nameof(ServerProcessId)
            && e.PropertyName != nameof(ServerStatusText)
            && e.PropertyName != nameof(IsServerOperationInProgress)
            && e.PropertyName != nameof(ServerActionMessage)
            && e.PropertyName != nameof(MaxResponseTokensMinimum)
            && e.PropertyName != nameof(MaxResponseTokensMaximum)
            && e.PropertyName != nameof(MaxResponseTokensIncrement)
            && e.PropertyName != nameof(MaxResponseTokensRecommended)
            && (e.PropertyName == nameof(UseTextOnlyResponse) || !e.PropertyName!.StartsWith("Use", StringComparison.Ordinal))
            && !e.PropertyName.EndsWith("Error", StringComparison.Ordinal))
        {
            ValidateAll();
            OnPropertyChanged(nameof(IsModified));
            NotifyLaunchArgumentsChanged();
        }
    }

    private void ValidateAll()
    {
        _errors.Clear();

        string normalizedToolset = McpToolsetProfiles.NormalizeText(Toolset);
        if (!string.IsNullOrEmpty(normalizedToolset) && !McpToolsetProfiles.TryParse(normalizedToolset, out _))
        {
            _errors[nameof(Toolset)] = $"Allowed profiles (comma-separated): {string.Join(", ", McpToolsetProfiles.AllowedProfiles)}.";
        }

        if (!McpResponseTokenLimits.Contains(MaxResponseTokens))
        {
            _errors[nameof(MaxResponseTokens)] = $"Must be between {McpResponseTokenLimits.Minimum:N0} and {McpResponseTokenLimits.Maximum:N0}.";
        }

        ValidateBand(nameof(MaxActiveSessions), MaxActiveSessions, DiscussionLimits.ActiveSessionsBand);
        ValidateBand(nameof(MaxParticipantsPerSession), MaxParticipantsPerSession, DiscussionLimits.ParticipantsBand);
        ValidateBand(nameof(MaxMessagesPerSession), MaxMessagesPerSession, DiscussionLimits.MessagesBand);
        ValidateBand(nameof(MaxContextsPerSession), MaxContextsPerSession, DiscussionLimits.ContextsBand);
        ValidateBand(nameof(MaxMessageBytes), MaxMessageBytes, DiscussionLimits.MessageBytesBand);
        ValidateBand(nameof(MaxContextBytes), MaxContextBytes, DiscussionLimits.ContextBytesBand);

        if (MaxTotalBytes < DiscussionLimits.MinTotalBytes || MaxTotalBytes > DiscussionLimits.MaxTotalBytesAllowed)
        {
            _errors[nameof(MaxTotalBytes)] = $"Must be between {DiscussionLimits.MinTotalBytes:N0} and {DiscussionLimits.MaxTotalBytesAllowed:N0} bytes (1 MiB - 256 MiB).";
        }

        ValidateBand(nameof(MaxPostsPerMinute), MaxPostsPerMinute, DiscussionLimits.PostsPerMinuteBand);
        ValidateBand(nameof(IdleTimeoutSeconds), IdleTimeoutSeconds, DiscussionLimits.IdleTimeoutBand);
        ValidateBand(nameof(MaxSessionAgeSeconds), MaxSessionAgeSeconds, DiscussionLimits.MaxSessionAgeBand);
        ValidateBand(nameof(ClosedRetentionSeconds), ClosedRetentionSeconds, DiscussionLimits.ClosedRetentionBand);
        ValidateBand(nameof(AwaySeconds), AwaySeconds, DiscussionLimits.AwayBand);
        ValidateBand(nameof(FloorTimeoutSeconds), FloorTimeoutSeconds, DiscussionLimits.FloorTimeoutBand);
        ValidateBand(nameof(MaxWaitSeconds), MaxWaitSeconds, DiscussionLimits.WaitBand);
        ValidateBand(nameof(MaxReadLimit), MaxReadLimit, DiscussionLimits.MaxReadLimitBand);

        if (DefaultReadLimit < DiscussionLimits.MinDefaultReadLimit || DefaultReadLimit > MaxReadLimit)
        {
            _errors[nameof(DefaultReadLimit)] = $"Must be between {DiscussionLimits.MinDefaultReadLimit} and {MaxReadLimit} (MaxReadLimit).";
        }

        ValidateBand(nameof(AuditCapacity), AuditCapacity, DiscussionLimits.AuditCapacityBand);
        ValidateBand(nameof(CommandLogCapacity), CommandLogCapacity, DiscussionLimits.AuditCapacityBand);
        ValidateBand(nameof(CommandLogMaxFileBytes), CommandLogMaxFileBytes, DiscussionLimits.CommandLogFileBytesBand);
        ValidateBand(nameof(CommandLogGenerations), CommandLogGenerations, DiscussionLimits.CommandLogGenerationsBand);
        ValidateBand(nameof(CommandLogMaxAgeHours), CommandLogMaxAgeHours, DiscussionLimits.CommandLogMaxAgeHoursBand);

        RaiseErrorPropertiesChanged();
    }

    private void ValidateBand(string propertyName, int value, DiscussionBand band)
    {
        if (!band.Contains(value))
        {
            _errors[propertyName] = $"Must be between {band.Minimum} and {band.Maximum}.";
        }
    }

    private void RaiseErrorPropertiesChanged()
    {
        OnPropertyChanged(nameof(ToolsetError));
        OnPropertyChanged(nameof(MaxResponseTokensError));
        OnPropertyChanged(nameof(MaxActiveSessionsError));
        OnPropertyChanged(nameof(MaxParticipantsPerSessionError));
        OnPropertyChanged(nameof(MaxMessagesPerSessionError));
        OnPropertyChanged(nameof(MaxContextsPerSessionError));
        OnPropertyChanged(nameof(MaxMessageBytesError));
        OnPropertyChanged(nameof(MaxContextBytesError));
        OnPropertyChanged(nameof(MaxTotalBytesError));
        OnPropertyChanged(nameof(MaxPostsPerMinuteError));
        OnPropertyChanged(nameof(IdleTimeoutSecondsError));
        OnPropertyChanged(nameof(MaxSessionAgeSecondsError));
        OnPropertyChanged(nameof(ClosedRetentionSecondsError));
        OnPropertyChanged(nameof(AwaySecondsError));
        OnPropertyChanged(nameof(FloorTimeoutSecondsError));
        OnPropertyChanged(nameof(MaxWaitSecondsError));
        OnPropertyChanged(nameof(DefaultReadLimitError));
        OnPropertyChanged(nameof(MaxReadLimitError));
        OnPropertyChanged(nameof(AuditCapacityError));
        OnPropertyChanged(nameof(CommandLogCapacityError));
        OnPropertyChanged(nameof(CommandLogMaxFileBytesError));
        OnPropertyChanged(nameof(CommandLogGenerationsError));
        OnPropertyChanged(nameof(CommandLogMaxAgeHoursError));
    }

    public async Task SaveChangesAsync()
    {
        await TrySaveCoreAsync();
    }

    private async Task<bool> TrySaveCoreAsync()
    {
        ValidateAll();
        if (!IsValid)
        {
            ValidationMessage = Localize("McpServer_Error_SaveInvalid", "Cannot save settings: some parameters are outside permitted bounds.");
            return false;
        }

        try
        {
            var settings = BuildSettings();
            await _manager.SaveAsync(settings);
            _snapshot = settings.Clone();
            ValidationMessage = string.Empty;
            OnPropertyChanged(nameof(IsModified));
            NotifyLaunchArgumentsChanged();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save McpServer settings.");
            ValidationMessage = $"Failed to save settings: {ex.Message}";
            return false;
        }
    }

    private bool CanStopServer() => _processService is not null && !IsServerOperationInProgress && IsServerRunning;

    [RelayCommand(CanExecute = nameof(CanStopServer))]
    private void StopServer()
    {
        if (_processService is null)
        {
            ServerActionMessage = Localize("McpServer_Msg_ProcessServiceUnavailable", "McpServer process control service is unavailable.");
            return;
        }

        IsServerOperationInProgress = true;
        try
        {
            _processService.StopServer();
            ServerActionMessage = Localize("McpServer_Msg_Stopped", "Stopped StockAnalyzer.McpServer.");
            NotifyServerStateChanged();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to stop StockAnalyzer.McpServer.");
            ServerActionMessage = ex.Message;
        }
        finally
        {
            IsServerOperationInProgress = false;
        }
    }

    private bool CanRestartServer() => _processService is not null && !IsServerOperationInProgress;

    [RelayCommand(CanExecute = nameof(CanRestartServer))]
    private async Task RestartServerAsync()
    {
        if (_processService is null)
        {
            ServerActionMessage = Localize("McpServer_Msg_ProcessServiceUnavailable", "McpServer process control service is unavailable.");
            return;
        }

        ValidateAll();
        if (!IsValid)
        {
            ValidationMessage = Localize("McpServer_Error_RestartInvalid", "Cannot restart McpServer: some parameters are outside permitted bounds.");
            ServerActionMessage = ValidationMessage;
            return;
        }

        IsServerOperationInProgress = true;
        try
        {
            if (IsModified && !await TrySaveCoreAsync())
            {
                ServerActionMessage = ValidationMessage;
                return;
            }

            McpServerLaunchResult result = await _processService.RestartServerAsync();
            ServerActionMessage = result.Message ?? (result.Success
                ? Localize("McpServer_Msg_Restarted", "Restarted StockAnalyzer.McpServer.")
                : Localize("McpServer_Msg_RestartFailed", "Failed to restart StockAnalyzer.McpServer."));
            NotifyServerStateChanged();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to restart StockAnalyzer.McpServer.");
            ServerActionMessage = ex.Message;
            NotifyServerStateChanged();
        }
        finally
        {
            IsServerOperationInProgress = false;
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        if (_processService is not null)
        {
            _processService.PropertyChanged -= OnProcessServicePropertyChanged;
        }
        GC.SuppressFinalize(this);
    }

    public void RevertChanges()
    {
        ApplySettings(_snapshot);
        ValidationMessage = string.Empty;
    }

    public void ResetToDefault()
    {
        var defaults = new McpServerSettings();
        ApplySettings(defaults);
        ValidationMessage = string.Empty;
    }
}
