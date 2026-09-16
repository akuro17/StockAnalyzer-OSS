using System;
using System.Collections.Generic;
using System.ComponentModel;
using Xunit;
using CommunityToolkit.Mvvm.Messaging;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Avalonia.Tests.Services;
using System.Linq;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

public class IndicatorPropertiesViewModelTests
{
    [Fact]
    public void PropertyChanged_DoesNotAutomaticallySendApplyMessage()
    {
        // Arrange
        var messenger = new StrongReferenceMessenger();
        var dispatcher = new SynchronousDispatcherService();
        var indicator = DefaultCoreIndicatorSettings.GetDefault()
            .First(s => s.TypeEnum == IndicatorType.SMA);
        indicator.IsEnabled = true;

        var sut = new IndicatorPropertiesViewModel(indicator, messenger, dispatcher);
        bool messageReceived = false;
        messenger.Register<SingleIndicatorSettingsChangedMessage>(this, (r, m) =>
        {
            messageReceived = true;
        });

        // Act - modify parameter
        if (sut.EditingSettings.ParameterObject is CoreSmaParameter smaParam)
        {
            smaParam.Period = 50;
        }
        sut.EditingSettings.DisplayName = "New SMA 50";

        // Assert - message should NOT be sent automatically
        Assert.False(messageReceived);
    }

    [Fact]
    public void ApplyCommand_SendsSingleIndicatorSettingsChangedMessage()
    {
        // Arrange
        var messenger = new StrongReferenceMessenger();
        var dispatcher = new SynchronousDispatcherService();
        var indicator = DefaultCoreIndicatorSettings.GetDefault()
            .First(s => s.TypeEnum == IndicatorType.SMA);

        var sut = new IndicatorPropertiesViewModel(indicator, messenger, dispatcher);
        CoreIndicatorSettings? receivedSettings = null;
        messenger.Register<SingleIndicatorSettingsChangedMessage>(this, (r, m) =>
        {
            receivedSettings = m.Value;
        });

        if (sut.EditingSettings.ParameterObject is CoreSmaParameter smaParam)
        {
            smaParam.Period = 42;
        }

        // Act
        sut.ApplyCommand.Execute(null);

        // Assert
        Assert.NotNull(receivedSettings);
        var receivedParam = Assert.IsType<CoreSmaParameter>(receivedSettings.ParameterObject);
        Assert.Equal(42, receivedParam.Period);
    }

    [Fact]
    public void OkCommand_SendsMessageAndSetsDialogResultTrue()
    {
        // Arrange
        var messenger = new StrongReferenceMessenger();
        var dispatcher = new SynchronousDispatcherService();
        var indicator = DefaultCoreIndicatorSettings.GetDefault()
            .First(s => s.TypeEnum == IndicatorType.SMA);

        var sut = new IndicatorPropertiesViewModel(indicator, messenger, dispatcher);
        bool? closedResult = null;
        sut.RequestClose = res => closedResult = res;

        bool messageReceived = false;
        messenger.Register<SingleIndicatorSettingsChangedMessage>(this, (r, m) =>
        {
            messageReceived = true;
        });

        // Act
        sut.OkCommand.Execute(null);

        // Assert
        Assert.True(messageReceived);
        Assert.True(sut.DialogResult);
        Assert.True(closedResult);
    }

    [Fact]
    public void CancelCommand_DoesNotSendMessageAndSetsDialogResultFalse()
    {
        // Arrange
        var messenger = new StrongReferenceMessenger();
        var dispatcher = new SynchronousDispatcherService();
        var indicator = DefaultCoreIndicatorSettings.GetDefault()
            .First(s => s.TypeEnum == IndicatorType.SMA);

        var sut = new IndicatorPropertiesViewModel(indicator, messenger, dispatcher);
        bool? closedResult = null;
        sut.RequestClose = res => closedResult = res;

        bool messageReceived = false;
        messenger.Register<SingleIndicatorSettingsChangedMessage>(this, (r, m) =>
        {
            messageReceived = true;
        });

        // Act
        sut.CancelCommand.Execute(null);

        // Assert
        Assert.False(messageReceived);
        Assert.False(sut.DialogResult);
        Assert.False(closedResult);
    }

    [Fact]
    public void HasDynamicPeriodDriver_TurningOnWithNoAvailableDrivers_StaysOff_AndNotifiesPropertyChanged()
    {
        // Regression for the constraint-check finding M-1b: with no allIndicators list supplied,
        // AvailableDynamicPeriodDrivers contains only the "(None)" sentinel, so there is no
        // candidate driver. The setter's early-return branch must still raise PropertyChanged
        // so a bound CheckBox re-reads the getter (false) instead of staying visually checked.
        var messenger = new StrongReferenceMessenger();
        var dispatcher = new SynchronousDispatcherService();
        var indicator = DefaultCoreIndicatorSettings.GetDefault()
            .First(s => s.TypeEnum == IndicatorType.SMA);

        var sut = new IndicatorPropertiesViewModel(indicator, messenger, dispatcher);

        var raisedProperties = new List<string?>();
        ((INotifyPropertyChanged)sut).PropertyChanged += (_, e) => raisedProperties.Add(e.PropertyName);

        sut.HasDynamicPeriodDriver = true;

        Assert.False(sut.HasDynamicPeriodDriver);
        Assert.True(string.IsNullOrEmpty(sut.EditingSettings.DynamicPeriodIndicatorId));
        Assert.Contains(nameof(IndicatorPropertiesViewModel.HasDynamicPeriodDriver), raisedProperties);
    }

    [Fact]
    public void HiddenParameterTags_StaysEmpty_WhenDynamicPeriodDriverIsActive()
    {
        // Period remains editable even when a Dynamic Period Driver is assigned: the static value
        // is still used as AdaptiveSmoothingHelper.CalculateAdaptiveSma's defaultPeriod fallback for
        // bars where the driver has no value yet, so it must stay visible/editable, not hidden.
        var messenger = new StrongReferenceMessenger();
        var dispatcher = new SynchronousDispatcherService();
        var indicator = DefaultCoreIndicatorSettings.GetDefault()
            .First(s => s.TypeEnum == IndicatorType.SMA);
        indicator.DynamicPeriodIndicatorId = "SomeDriverId";

        var sut = new IndicatorPropertiesViewModel(indicator, messenger, dispatcher);

        Assert.True(sut.HasDynamicPeriodDriver);
        Assert.Empty(sut.HiddenParameterTags);
    }
}
