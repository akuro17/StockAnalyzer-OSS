using System;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Models.Notes;

namespace StockAnalyzer.Avalonia.ViewModels.Notes;

/// <summary>
/// Presentation wrapper for a Note appearing in the Upcoming Catalysts sidebar.
/// Carries pre-computed remaining day count, countdown badge text, and navigation callback.
/// </summary>
public sealed partial class UpcomingCatalystItemViewModel : ObservableObject
{
    private const int UrgentRemainingDaysThreshold = 2;
    private const int DefaultMaxBodySnippetChars = 60;

    public Note Note { get; }
    public DateTime CatalystDate { get; }
    public int RemainingDays { get; }
    public string FormattedDate { get; }
    public string CountdownText { get; }
    public string Ticker { get; }
    public string BodySnippet { get; }
    public bool IsUrgent => RemainingDays <= UrgentRemainingDaysThreshold;
    public IRelayCommand NavigateCommand { get; }

    public UpcomingCatalystItemViewModel(
        Note note,
        DateTime today,
        Action<Note> onNavigate)
    {
        ArgumentNullException.ThrowIfNull(note);
        ArgumentNullException.ThrowIfNull(onNavigate);

        Note = note;
        CatalystDate = note.CatalystDate?.Date ?? today.Date;
        RemainingDays = (CatalystDate - today.Date).Days;
        FormattedDate = CatalystDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        CountdownText = FormatCountdown(RemainingDays);
        Ticker = note.RelatedTicker ?? string.Empty;
        BodySnippet = ExtractSnippet(note.Body, maxChars: DefaultMaxBodySnippetChars);
        NavigateCommand = new RelayCommand(() => onNavigate(note));
    }

    private static string FormatCountdown(int days)
    {
        if (days == 0)
        {
            return LocalizationManager.Instance.TryGet("Note_Catalyst_Today", out var loc) ? loc : "Today";
        }
        if (days == 1)
        {
            return LocalizationManager.Instance.TryGet("Note_Catalyst_Tomorrow", out var loc) ? loc : "Tomorrow";
        }
        if (days > 1)
        {
            var template = LocalizationManager.Instance.TryGet("Note_Catalyst_DaysLeft", out var loc) ? loc : "{0} days left";
            return string.Format(template, days);
        }
        if (days == -1)
        {
            return LocalizationManager.Instance.TryGet("Note_Catalyst_Yesterday", out var loc) ? loc : "Yesterday";
        }

        var overdueTemplate = LocalizationManager.Instance.TryGet("Note_Catalyst_DaysOverdue", out var overdueLoc) ? overdueLoc : "{0} days overdue";
        return string.Format(overdueTemplate, Math.Abs(days));
    }

    private static string ExtractSnippet(string body, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        var span = body.AsSpan().Trim();
        var newlineIndex = span.IndexOfAny('\r', '\n');
        if (newlineIndex >= 0)
        {
            span = span.Slice(0, newlineIndex).Trim();
        }

        if (span.IsEmpty)
        {
            return string.Empty;
        }

        return span.Length <= maxChars
            ? span.ToString()
            : string.Concat(span.Slice(0, maxChars), "...");
    }
}
