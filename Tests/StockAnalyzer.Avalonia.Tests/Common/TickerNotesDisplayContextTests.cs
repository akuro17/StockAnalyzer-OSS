using System;
using CommunityToolkit.Mvvm.Messaging;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Tests.TestHelpers;
using StockAnalyzer.Core.Models.Settings;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Common;

/// <summary>
/// TickerNotesDisplayContext is process-wide static state: every test class that reads or mutates it
/// shares the "TickerNotesDisplayContext State" collection so they never run in parallel.
/// </summary>
[Collection("TickerNotesDisplayContext State")]
public class TickerNotesDisplayContextTests : IDisposable
{
    public TickerNotesDisplayContextTests() => TickerNotesDisplayContext.ResetForTesting();

    public void Dispose() => TickerNotesDisplayContext.ResetForTesting();

    private sealed class MessageCounter : IDisposable
    {
        private readonly IMessenger _messenger;
        private readonly object _recipient = new();

        public MessageCounter(IMessenger messenger)
        {
            _messenger = messenger;
            _messenger.Register<object, NotesReadMoreThresholdChangedMessage>(_recipient, (_, _) => Count++);
        }

        public int Count { get; private set; }

        public void Dispose() => _messenger.UnregisterAll(_recipient);
    }

    [Fact]
    public void WhenNotInitialized_ReturnsSettingsConstantDefaults()
    {
        Assert.Equal(NotesSettingsConstants.DefaultReadMoreMaxCharacters, TickerNotesDisplayContext.MaxCharacters);
        Assert.Equal(NotesSettingsConstants.DefaultReadMoreMaxLines, TickerNotesDisplayContext.MaxLines);
    }

    [Fact]
    public void WhenInitialized_ReflectsManagerValuesLive()
    {
        var manager = new FakeNotesSettingsManager();
        TickerNotesDisplayContext.Initialize(manager, new WeakReferenceMessenger());

        manager.SetReadMoreMaxCharacters(42);
        manager.SetReadMoreMaxLines(3);

        Assert.Equal(42, TickerNotesDisplayContext.MaxCharacters);
        Assert.Equal(3, TickerNotesDisplayContext.MaxLines);
    }

    [Fact]
    public void ThresholdChange_SendsExactlyOneMessagePerChange_ThroughTheInjectedMessenger()
    {
        var manager = new FakeNotesSettingsManager();
        var messenger = new WeakReferenceMessenger();
        TickerNotesDisplayContext.Initialize(manager, messenger);
        using var counter = new MessageCounter(messenger);

        manager.SetReadMoreMaxCharacters(42);
        manager.SetReadMoreMaxLines(3);

        Assert.Equal(2, counter.Count);
    }

    [Fact]
    public void ThresholdChange_DoesNotReachAnotherMessenger()
    {
        var manager = new FakeNotesSettingsManager();
        TickerNotesDisplayContext.Initialize(manager, new WeakReferenceMessenger());
        var other = new WeakReferenceMessenger();
        using var counter = new MessageCounter(other);

        manager.SetReadMoreMaxCharacters(42);

        Assert.Equal(0, counter.Count);
    }

    [Fact]
    public void UnrelatedSettingChange_SendsNoMessage()
    {
        var manager = new FakeNotesSettingsManager();
        var messenger = new WeakReferenceMessenger();
        TickerNotesDisplayContext.Initialize(manager, messenger);
        using var counter = new MessageCounter(messenger);

        manager.SetBodyFontSize(20.0);
        manager.SetTimelinePageSize(5);

        Assert.Equal(0, counter.Count);
    }

    [Fact]
    public void InitializeTwice_StillSendsOneMessagePerChange_AndIgnoresThePreviousManager()
    {
        var first = new FakeNotesSettingsManager();
        var second = new FakeNotesSettingsManager();
        var messenger = new WeakReferenceMessenger();
        TickerNotesDisplayContext.Initialize(first, messenger);
        TickerNotesDisplayContext.Initialize(second, messenger);
        using var counter = new MessageCounter(messenger);

        second.SetReadMoreMaxCharacters(42);
        first.SetReadMoreMaxCharacters(7);

        Assert.Equal(1, counter.Count);
        Assert.Equal(42, TickerNotesDisplayContext.MaxCharacters);
    }

    [Fact]
    public void ReadMoreLabel_UsesTheGivenProvider_ElseTheLocalizedNoteReadMoreLabel()
    {
        Assert.Equal(NoteReadMoreLabel.Get(), TickerNotesDisplayContext.ReadMoreLabel);

        TickerNotesDisplayContext.Initialize(new FakeNotesSettingsManager(), new WeakReferenceMessenger(), () => "PROVIDED");
        Assert.Equal("PROVIDED", TickerNotesDisplayContext.ReadMoreLabel);

        TickerNotesDisplayContext.Initialize(new FakeNotesSettingsManager(), new WeakReferenceMessenger());
        Assert.Equal(NoteReadMoreLabel.Get(), TickerNotesDisplayContext.ReadMoreLabel);
    }

    [Fact]
    public void Initialize_WithNullArguments_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => TickerNotesDisplayContext.Initialize(null!, new WeakReferenceMessenger()));
        Assert.Throws<ArgumentNullException>(() => TickerNotesDisplayContext.Initialize(new FakeNotesSettingsManager(), null!));
    }
}
