using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// BASELINE, NOT A PROMISE: the watchlist keeps the spelling its writer passes (trim and upper-case only; a dot stays a dot, no Japanese suffix is added) and deduplicates by
/// case-insensitive comparison. Today every writer passes an already normalised symbol (the Add Ticker dialog, the bulk import), so the difference is invisible; the rows exist so that a later
/// decision to canonicalise the watchlist (backlog) flips them knowingly. Before such a change, inspect the saved watchlist files for dotted keys.
/// </summary>
public class WatchlistManagerKeySpellingTests
{
    private static string[] TickersAfterAdding(params string[] tickers)
    {
        var manager = new WatchlistManager();
        var profile = manager.CreateProfile("Spelling", IndicatorColor.Gray);
        foreach (string ticker in tickers) manager.AddTickerToProfile(profile.Id, ticker);
        return manager.GetProfileById(profile.Id)!.Items.Select(i => i.Ticker).ToArray();
    }

    [Fact]
    public void ADottedKeyStaysDotted_AndIsDeduplicatedCaseInsensitively()
    {
        Assert.Equal(new[] { "BRK.B" }, TickersAfterAdding("brk.b", "BRK.B", " Brk.B "));
    }

    [Fact]
    public void TheDottedAndTheHyphenatedSpellingsAreTwoKeys()
    {
        Assert.Equal(new[] { "BRK.B", "BRK-B" }, TickersAfterAdding("BRK.B", "BRK-B"));
    }

    [Fact]
    public void AFourDigitCodeGetsNoJapaneseSuffix()
    {
        Assert.Equal(new[] { "7203" }, TickersAfterAdding("7203"));
    }
}
