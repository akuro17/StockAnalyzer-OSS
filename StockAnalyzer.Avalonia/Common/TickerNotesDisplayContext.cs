using System;
using System.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Services.Notes;

namespace StockAnalyzer.Avalonia.Common;

/// <summary>
/// Live read-only view of Settings &gt; Notes "Read More Threshold" (and the localized "Read more" label) for the
/// Tickers-tab Notes cell and popup. <c>WatchlistItemViewModel</c> rows are created with <c>new</c> at several sites
/// in two ViewModels (no constructor DI possible) and already read <c>UserStrategyMetadataRepository.Instance</c>,
/// so this static context - the narrow "Static Context Fallback for Non-DI Object Hierarchies" exception in
/// SA_ARCHITECTURE_RULES.md, same pattern as <c>DrawingThemeContext</c> - exposes only those read-only values.
/// Values are never cached: every read goes to the settings manager, so a Settings change is visible on the
/// very next read; until <see cref="Initialize"/> runs the <see cref="NotesSettingsConstants"/> defaults apply.
/// A threshold change is announced with <see cref="NotesReadMoreThresholdChangedMessage"/> on the injected messenger.
/// </summary>
public static class TickerNotesDisplayContext
{
    private static readonly object Gate = new();
    private static volatile INotesSettingsManager? _manager;
    private static volatile IMessenger? _messenger;
    private static volatile Func<string>? _readMoreLabelProvider;

    public static int MaxCharacters => _manager?.ReadMoreMaxCharacters ?? NotesSettingsConstants.DefaultReadMoreMaxCharacters;

    public static int MaxLines => _manager?.ReadMoreMaxLines ?? NotesSettingsConstants.DefaultReadMoreMaxLines;

    /// <summary>The localized "Read more" wording (<see cref="NoteReadMoreLabel"/> unless a provider was given to
    /// <see cref="Initialize"/>).</summary>
    public static string ReadMoreLabel => (_readMoreLabelProvider ?? NoteReadMoreLabel.Get)();

    /// <summary>Binds the context to <paramref name="manager"/> and announces threshold changes on
    /// <paramref name="messenger"/>. A repeated call replaces the previous manager and unsubscribes from it, so one
    /// threshold change never produces two messages. <paramref name="readMoreLabelProvider"/> overrides the label
    /// source (tests); production leaves it null.</summary>
    public static void Initialize(INotesSettingsManager manager, IMessenger messenger, Func<string>? readMoreLabelProvider = null)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(messenger);

        lock (Gate)
        {
            if (_manager is not null)
            {
                _manager.PropertyChanged -= OnManagerPropertyChanged;
            }

            _messenger = messenger;
            _readMoreLabelProvider = readMoreLabelProvider;
            _manager = manager;
            _manager.PropertyChanged += OnManagerPropertyChanged;
        }
    }

    /// <summary>Testability seam: returns the context to its uninitialized (defaults) state.</summary>
    internal static void ResetForTesting()
    {
        lock (Gate)
        {
            if (_manager is not null)
            {
                _manager.PropertyChanged -= OnManagerPropertyChanged;
            }

            _manager = null;
            _messenger = null;
            _readMoreLabelProvider = null;
        }
    }

    private static void OnManagerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // A null/empty name means "every property changed" (INotifyPropertyChanged contract).
        if (string.IsNullOrEmpty(e.PropertyName) ||
            e.PropertyName == nameof(INotesSettingsManager.ReadMoreMaxCharacters) ||
            e.PropertyName == nameof(INotesSettingsManager.ReadMoreMaxLines))
        {
            _messenger?.Send(new NotesReadMoreThresholdChangedMessage());
        }
    }
}
