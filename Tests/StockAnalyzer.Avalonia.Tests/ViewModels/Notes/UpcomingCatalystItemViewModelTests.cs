using System;
using System.Collections.Immutable;
using StockAnalyzer.Avalonia.ViewModels.Notes;
using StockAnalyzer.Core.Models.Notes;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels.Notes;

public class UpcomingCatalystItemViewModelTests
{
    private static Note MakeNote(string body, DateTime? catalystDate, string? ticker = "AAPL")
    {
        return new Note(
            Id: Guid.NewGuid(),
            Body: body,
            CreatedAt: DateTime.UtcNow,
            UpdatedAt: DateTime.UtcNow)
        {
            RelatedTicker = ticker,
            CatalystDate = catalystDate,
        };
    }

    [Fact]
    public void Construction_CalculatesRemainingDaysAndFormattedDate()
    {
        var today = new DateTime(2026, 10, 5);
        var targetDate = new DateTime(2026, 10, 10);
        var note = MakeNote("Earnings release announcement", targetDate, "MSFT");

        var vm = new UpcomingCatalystItemViewModel(note, today, _ => { });

        Assert.Equal(5, vm.RemainingDays);
        Assert.Equal("2026-10-10", vm.FormattedDate);
        Assert.Equal("MSFT", vm.Ticker);
        Assert.False(vm.IsUrgent);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(-1, true)]
    public void IsUrgent_ReturnsTrueWhenRemainingDaysLessThanOrEqualToTwo(int offsetDays, bool expectedUrgent)
    {
        var today = new DateTime(2026, 10, 5);
        var note = MakeNote("Event", today.AddDays(offsetDays));

        var vm = new UpcomingCatalystItemViewModel(note, today, _ => { });

        Assert.Equal(expectedUrgent, vm.IsUrgent);
    }

    [Fact]
    public void CountdownText_FormatsCorrectlyForFutureAndPastDates()
    {
        var today = new DateTime(2026, 10, 5);

        var todayVm = new UpcomingCatalystItemViewModel(MakeNote("Event", today), today, _ => { });
        Assert.Contains("Today", todayVm.CountdownText, StringComparison.OrdinalIgnoreCase);

        var tomorrowVm = new UpcomingCatalystItemViewModel(MakeNote("Event", today.AddDays(1)), today, _ => { });
        Assert.Contains("Tomorrow", tomorrowVm.CountdownText, StringComparison.OrdinalIgnoreCase);

        var futureVm = new UpcomingCatalystItemViewModel(MakeNote("Event", today.AddDays(5)), today, _ => { });
        Assert.Contains("5", futureVm.CountdownText);

        var yesterdayVm = new UpcomingCatalystItemViewModel(MakeNote("Event", today.AddDays(-1)), today, _ => { });
        Assert.Contains("Yesterday", yesterdayVm.CountdownText, StringComparison.OrdinalIgnoreCase);

        var pastVm = new UpcomingCatalystItemViewModel(MakeNote("Event", today.AddDays(-3)), today, _ => { });
        Assert.Contains("3", pastVm.CountdownText);
        Assert.DoesNotContain("-", pastVm.CountdownText);
    }

    [Fact]
    public void ExtractSnippet_TrimsWhitespaceAndFirstLine()
    {
        var today = new DateTime(2026, 10, 5);
        var note = MakeNote("First line of the catalyst note\r\nSecond line should not be included", today);

        var vm = new UpcomingCatalystItemViewModel(note, today, _ => { });

        Assert.Equal("First line of the catalyst note", vm.BodySnippet);
    }

    [Fact]
    public void ExtractSnippet_TruncatesLongLines()
    {
        var today = new DateTime(2026, 10, 5);
        var longLine = new string('A', 80);
        var note = MakeNote(longLine, today);

        var vm = new UpcomingCatalystItemViewModel(note, today, _ => { });

        Assert.Equal(new string('A', 60) + "...", vm.BodySnippet);
    }

    [Fact]
    public void NavigateCommand_InvokesCallbackWithNote()
    {
        var today = new DateTime(2026, 10, 5);
        var note = MakeNote("Clickable catalyst", today);
        Note? navigated = null;

        var vm = new UpcomingCatalystItemViewModel(note, today, n => navigated = n);
        vm.NavigateCommand.Execute(null);

        Assert.Same(note, navigated);
    }
}
