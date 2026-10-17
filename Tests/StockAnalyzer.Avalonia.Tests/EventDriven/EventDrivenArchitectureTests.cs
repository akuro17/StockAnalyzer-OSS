using CommunityToolkit.Mvvm.Messaging;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.EventDriven;

public class EventDrivenArchitectureTests : IDisposable
{
    // These tests exercise the messenger mechanics with the app's message types. They use their own messenger instance: the process-wide Default is shared with
    // every view model of the tests that run in parallel (a ChartViewModel sends ChartSettingsChangedMessage for its own settings, which overwrote the received value
    // here), and resetting Default would also drop the subscriptions of those tests.
    private readonly WeakReferenceMessenger _messenger = new();

    public void Dispose() => _messenger.Reset();

    [Fact]
    public void CrosshairPositionChangedMessage_ShouldNotifySubscribers()
    {
        // Arrange
        CrosshairPositionData? receivedData = null;
        _messenger.Register<CrosshairPositionChangedMessage>(this, (_, msg) =>
        {
            receivedData = msg.Value;
        });

        var candle = new CoreCandleData(DateTime.Now, 100m, 110m, 90m, 105m, 1000);
        var expectedData = new CrosshairPositionData { CandleIndex = 42, HoveredCandle = candle, ChartSymbol = "AAPL" };

        // Act
        _messenger.Send(new CrosshairPositionChangedMessage(expectedData));

        // Assert
        Assert.NotNull(receivedData);
        Assert.Equal(42, receivedData!.CandleIndex);
        Assert.Equal("AAPL", receivedData.ChartSymbol);
        Assert.Equal(105m, receivedData.HoveredCandle!.Close);
    }

    [Fact]
    public void CrosshairPositionChangedMessage_ShouldSendNullCandleOnExit()
    {
        // Arrange
        CrosshairPositionData? receivedData = null;
        _messenger.Register<CrosshairPositionChangedMessage>(this, (_, msg) =>
        {
            receivedData = msg.Value;
        });

        var exitData = new CrosshairPositionData { CandleIndex = -1, HoveredCandle = null, ChartSymbol = "AAPL" };

        // Act
        _messenger.Send(new CrosshairPositionChangedMessage(exitData));

        // Assert
        Assert.NotNull(receivedData);
        Assert.Equal(-1, receivedData!.CandleIndex);
        Assert.Null(receivedData.HoveredCandle);
    }

    [Fact]
    public void ChartSettingsChangedMessage_ShouldNotifySubscribers()
    {
        // Arrange
        ChartSettingChange? receivedChange = null;
        _messenger.Register<ChartSettingsChangedMessage>(this, (_, msg) =>
        {
            receivedChange = msg.Value;
        });

        var change = new ChartSettingChange("ShowHeaderInfo", true);

        // Act
        _messenger.Send(new ChartSettingsChangedMessage(change));

        // Assert
        Assert.NotNull(receivedChange);
        Assert.Equal("ShowHeaderInfo", receivedChange!.SettingName);
        Assert.Equal(true, receivedChange.NewValue);
    }

    [Fact]
    public void MultipleSubscribers_ShouldAllReceiveMessages()
    {
        // Arrange
        int receiveCount = 0;
        var subscriber1 = new object();
        var subscriber2 = new object();

        _messenger.Register<CrosshairPositionChangedMessage>(subscriber1, (_, _) =>
        {
            Interlocked.Increment(ref receiveCount);
        });
        _messenger.Register<CrosshairPositionChangedMessage>(subscriber2, (_, _) =>
        {
            Interlocked.Increment(ref receiveCount);
        });

        var data = new CrosshairPositionData { CandleIndex = 10, HoveredCandle = null, ChartSymbol = "MSFT" };

        // Act
        _messenger.Send(new CrosshairPositionChangedMessage(data));

        // Assert
        Assert.Equal(2, receiveCount);

        // Cleanup
        _messenger.Unregister<CrosshairPositionChangedMessage>(subscriber1);
        _messenger.Unregister<CrosshairPositionChangedMessage>(subscriber2);
    }

    [Fact]
    public void UnregisteredSubscriber_ShouldNotReceiveMessages()
    {
        // Arrange
        int receiveCount = 0;
        var subscriber = new object();

        _messenger.Register<CrosshairPositionChangedMessage>(subscriber, (_, _) =>
        {
            Interlocked.Increment(ref receiveCount);
        });

        // Unregister before sending
        _messenger.Unregister<CrosshairPositionChangedMessage>(subscriber);

        var data = new CrosshairPositionData { CandleIndex = 10, HoveredCandle = null, ChartSymbol = "GOOG" };

        // Act
        _messenger.Send(new CrosshairPositionChangedMessage(data));

        // Assert
        Assert.Equal(0, receiveCount);
    }
}
