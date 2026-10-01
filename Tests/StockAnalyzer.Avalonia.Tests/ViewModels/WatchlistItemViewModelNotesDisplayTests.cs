using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.Messaging;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Tests.TestHelpers;
using StockAnalyzer.Avalonia.ViewModels.Watchlist;
using StockAnalyzer.Core.Services.Notes;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

/// <summary>Tickers-tab Notes cell / popup text derived from the Read More Threshold (see
/// <see cref="TickerNotesDisplayContext"/>); shares the context's collection because it mutates that static state.</summary>
[Collection("TickerNotesDisplayContext State")]
public class WatchlistItemViewModelNotesDisplayTests : IDisposable
{
    // The popup label comes from the context's provider (localized wording in production); a fixed test value keeps
    // these tests independent of the process-wide LocalizationManager state.
    private const string Label = "MORE-LABEL";

    private readonly FakeNotesSettingsManager _settings = new();

    public WatchlistItemViewModelNotesDisplayTests()
    {
        TickerNotesDisplayContext.ResetForTesting();
        TickerNotesDisplayContext.Initialize(_settings, new WeakReferenceMessenger(), () => Label);
    }

    public void Dispose() => TickerNotesDisplayContext.ResetForTesting();

    private static WatchlistItemViewModel CreateViewModel() => new(
        "AAPL", "Apple Inc.", "Technology", "Consumer Electronics",
        150.0m, 155.0m, 149.0m, 153.0m, 1000000, 2.0, 3.0m);

    [Fact]
    public void DisplayNotes_OverMaxCharacters_IsCutToMaxCharacters()
    {
        _settings.SetReadMoreMaxCharacters(10);
        var vm = CreateViewModel();
        vm.Notes = new string('a', 30);

        Assert.Equal(new string('a', 10), vm.DisplayNotes);
    }

    [Fact]
    public void DisplayNotes_FlattensNewlinesBeforeCutting()
    {
        _settings.SetReadMoreMaxCharacters(5);
        var vm = CreateViewModel();
        vm.Notes = "abc\ndef";

        Assert.Equal("abc d", vm.DisplayNotes);
    }

    [Fact]
    public void NotesPopupText_WithinThresholds_IsTheBodyWithoutLabel()
    {
        var vm = CreateViewModel();
        vm.Notes = "short\nbody";

        Assert.Equal("short\nbody", vm.NotesPopupText);
    }

    [Fact]
    public void NotesPopupText_OverMaxCharacters_EndsWithTheProvidedLabelOnItsOwnLine()
    {
        _settings.SetReadMoreMaxCharacters(10);
        var vm = CreateViewModel();
        vm.Notes = new string('a', 30);

        Assert.Equal(new string('a', 10) + "\n" + Label, vm.NotesPopupText);
    }

    [Fact]
    public void NotesPopupText_OverMaxLines_EndsWithTheProvidedLabelOnItsOwnLine()
    {
        _settings.SetReadMoreMaxLines(1);
        var vm = CreateViewModel();
        vm.Notes = "1\n2\n3";

        // Max Lines = 1 keeps one line; the label goes on its own following line.
        Assert.Equal("1\n" + Label, vm.NotesPopupText);
    }

    [Fact]
    public void NotesPopupText_ExcludesImagesAndUrls()
    {
        var vm = CreateViewModel();
        vm.Notes = $"see {NoteImageTokenExtractor.Build(Guid.NewGuid())}https://example.com/x end";

        Assert.Equal("see  end", vm.NotesPopupText);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NotesPopupText_WhenNoNotes_IsEmpty_AndHasNoPopup(string? notes)
    {
        var vm = CreateViewModel();
        vm.Notes = notes;

        Assert.Equal(string.Empty, vm.NotesPopupText);
        Assert.False(vm.HasNotesPopupText);
        Assert.Equal("-", vm.DisplayNotes);
    }

    [Fact]
    public void NotesPopupText_WhenOnlyAnImageAndAUrl_IsEmpty_AndHasNoPopup()
    {
        var vm = CreateViewModel();
        vm.Notes = $"{NoteImageTokenExtractor.Build(Guid.NewGuid())} https://example.com/x";

        Assert.False(vm.HasNotesPopupText);
    }

    [Fact]
    public void HasNotesPopupText_WhenThereIsVisibleText_IsTrue()
    {
        var vm = CreateViewModel();
        vm.Notes = "text";

        Assert.True(vm.HasNotesPopupText);
    }

    [Fact]
    public void RaiseNotesDisplayChanged_RaisesTheNotesDerivedProperties_WithTheNewThreshold()
    {
        var vm = CreateViewModel();
        vm.Notes = new string('a', 30);
        Assert.Equal(new string('a', 30), vm.DisplayNotes);

        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        _settings.SetReadMoreMaxCharacters(10);
        vm.RaiseNotesDisplayChanged();

        Assert.Contains(nameof(WatchlistItemViewModel.DisplayNotes), raised);
        Assert.Contains(nameof(WatchlistItemViewModel.NotesPopupText), raised);
        Assert.Contains(nameof(WatchlistItemViewModel.HasNotesPopupText), raised);
        Assert.Equal(new string('a', 10), vm.DisplayNotes);
    }

    [Fact]
    public void SettingNotes_RaisesTheNotesDerivedProperties()
    {
        var vm = CreateViewModel();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.Notes = "x";

        Assert.Contains(nameof(WatchlistItemViewModel.NotesPopupText), raised);
        Assert.Contains(nameof(WatchlistItemViewModel.HasNotesPopupText), raised);
    }

    // The derived texts are cached per distinct input. "Same instance" is only provable for a body whose text has to be
    // BUILT (newlines are flattened / the popup text is rebuilt by StringBuilder); a body that needs no change is
    // returned as the very same string with or without a cache.
    [Fact]
    public void DisplayNotes_IsComputedOncePerInput_AndRecomputedWhenTheThresholdOrTheNotesChange()
    {
        _settings.SetReadMoreMaxCharacters(50);
        var vm = CreateViewModel();
        vm.Notes = "line one\nline two";

        var first = vm.DisplayNotes;

        Assert.Equal("line one line two", first);
        Assert.Same(first, vm.DisplayNotes);

        _settings.SetReadMoreMaxCharacters(8); // no message needed: the threshold is part of the cache key
        Assert.Equal("line one", vm.DisplayNotes);

        vm.Notes = "other\ntext";
        Assert.Equal("other te", vm.DisplayNotes);
    }

    [Fact]
    public void NotesPopupText_IsComputedOncePerInput_AndRecomputedWhenAnyInputChanges()
    {
        var currentLabel = Label;
        TickerNotesDisplayContext.ResetForTesting();
        TickerNotesDisplayContext.Initialize(_settings, new WeakReferenceMessenger(), () => currentLabel);
        _settings.SetReadMoreMaxCharacters(400);
        _settings.SetReadMoreMaxLines(5);
        var vm = CreateViewModel();
        vm.Notes = "line one\nline two\nline three";

        var first = vm.NotesPopupText;

        Assert.Equal("line one\nline two\nline three", first);
        Assert.Same(first, vm.NotesPopupText);

        _settings.SetReadMoreMaxLines(1);
        Assert.Equal("line one\n" + Label, vm.NotesPopupText);

        currentLabel = "OTHER-LABEL"; // e.g. the UI language changed
        Assert.Equal("line one\nOTHER-LABEL", vm.NotesPopupText);

        _settings.SetReadMoreMaxLines(5);
        _settings.SetReadMoreMaxCharacters(4);
        Assert.Equal("line\nOTHER-LABEL", vm.NotesPopupText);
    }

    [Fact]
    public void HasNotesPopupText_UsesTheSameCachedValue()
    {
        var vm = CreateViewModel();
        vm.Notes = "some\ntext";

        var text = vm.NotesPopupText;

        Assert.True(vm.HasNotesPopupText);
        Assert.Same(text, vm.NotesPopupText);
    }

    [Fact]
    public void DisplayReminder_LongText_IsNeverCut()
    {
        _settings.SetReadMoreMaxCharacters(10);
        var vm = CreateViewModel();
        var longReminder = new string('r', 500) + "\nsecond line";
        vm.Reminder = longReminder;

        Assert.Equal(longReminder, vm.DisplayReminder);
    }
}
