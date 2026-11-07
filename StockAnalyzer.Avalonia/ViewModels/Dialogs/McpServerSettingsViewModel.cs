using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Core.Discussion;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Avalonia.Services;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs;

/// <summary>
/// ViewModel for Settings > McpServer.
/// Manages user configuration of StockAnalyzer.McpServer launch options (--enable-discussion)
/// and the 22 tunable discussion hub parameters.
/// </summary>
public partial class McpServerSettingsViewModel : ViewModelBase, ISettingsPageViewModel
{
    private readonly IMcpServerSettingsManager _manager;
    private readonly ILogger<McpServerSettingsViewModel> _logger;
    private McpServerSettings _snapshot;
    private bool _isApplying;

    public string TitleKey => "Settings_McpServer";
    public string IconKey => "SettingsBacktestIcon";

    // Group A: Launch options
    [ObservableProperty] private bool _enableDiscussion;

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
        ILogger<McpServerSettingsViewModel>? logger = null)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _logger = logger ?? NullLogger<McpServerSettingsViewModel>.Instance;
        _snapshot = new McpServerSettings();

        LoadTask = LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            var loaded = await _manager.LoadAsync().ConfigureAwait(false);
            _snapshot = loaded.Clone();
            ApplySettings(_snapshot);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load McpServer settings in ViewModel.");
            _snapshot = new McpServerSettings();
            ApplySettings(_snapshot);
        }
    }

    private void ApplySettings(McpServerSettings s)
    {
        _isApplying = true;
        try
        {
            EnableDiscussion = s.EnableDiscussion;
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
    }

    public McpServerSettings BuildSettings()
    {
        return new McpServerSettings
        {
            EnableDiscussion = EnableDiscussion,
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

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        if (_isApplying) return;

        if (e.PropertyName != nameof(IsModified)
            && e.PropertyName != nameof(ValidationMessage)
            && e.PropertyName != nameof(HasValidationMessage)
            && !e.PropertyName!.EndsWith("Error", StringComparison.Ordinal))
        {
            ValidateAll();
            OnPropertyChanged(nameof(IsModified));
        }
    }

    private void ValidateAll()
    {
        _errors.Clear();

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
        ValidateAll();
        if (!IsValid)
        {
            ValidationMessage = "Cannot save settings: some parameters are outside permitted bounds.";
            return;
        }

        try
        {
            var settings = BuildSettings();
            await _manager.SaveAsync(settings).ConfigureAwait(false);
            _snapshot = settings.Clone();
            ValidationMessage = string.Empty;
            OnPropertyChanged(nameof(IsModified));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save McpServer settings.");
            ValidationMessage = $"Failed to save settings: {ex.Message}";
        }
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
