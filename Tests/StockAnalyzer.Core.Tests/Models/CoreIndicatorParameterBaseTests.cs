using System.ComponentModel;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Parameters;
using Xunit;

namespace StockAnalyzer.Core.Tests.Models;

public class CoreIndicatorParameterBaseTests
{
    [Fact]
    public void Validate_ShouldReturnErrors_WhenRangeIsViolated()
    {
        // Arrange
        var param = new CoreSmaParameter(); // Has [Range(1, 1000)]
        var notifyError = (INotifyDataErrorInfo)param;

        // Act
        param.Period = -1;
        var errors = notifyError.GetErrors(nameof(CoreSmaParameter.Period)).Cast<object>().ToList();

        // Assert
        Assert.NotEmpty(errors);
        Assert.Contains(errors, e => e.ToString().Contains("1") && e.ToString().Contains("1000"));
    }

    [Fact]
    public void Validate_ShouldReturnNoErrors_WhenValueIsValid()
    {
        // Arrange
        var param = new CoreSmaParameter();
        var notifyError = (INotifyDataErrorInfo)param;

        // Act
        param.Period = 14;
        var errors = notifyError.GetErrors(nameof(CoreSmaParameter.Period)).Cast<object>().ToList();

        // Assert
        Assert.Empty(errors);
    }

    [Fact]
    public void Clone_DoesNotInheritTheOriginalsPropertyChangedSubscribers()
    {
        var original = new CoreSmaParameter { Period = 20 };
        int originalNotifications = 0;
        original.PropertyChanged += (_, _) => originalNotifications++;

        var clone = (CoreSmaParameter)original.Clone();
        clone.Period = 50;

        Assert.Equal(0, originalNotifications);
        Assert.Equal(20, original.Period);
    }

    [Fact]
    public void Clone_KeepsValuesAndNotifiesItsOwnSubscribers()
    {
        var original = new CoreSmaParameter { Period = 20 };
        var clone = (CoreSmaParameter)original.Clone();
        var changed = new System.Collections.Generic.List<string?>();
        clone.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Assert.Equal(20, clone.Period);
        clone.Period = 30;

        Assert.Equal(new[] { nameof(CoreSmaParameter.Period) }, changed);
    }

    [Fact]
    public void SettingsClone_EditingTheClonedParameter_DoesNotRaiseChangesOnTheOriginalSettings()
    {
        var original = new CoreIndicatorSettings { ParameterObject = new CoreSmaParameter { Period = 20 } };
        long originalVersion = original.MathematicalVersion;
        var originalRaised = new System.Collections.Generic.List<string?>();
        original.PropertyChanged += (_, e) => originalRaised.Add(e.PropertyName);

        var clone = original.Clone();
        ((CoreSmaParameter)clone.ParameterObject!).Period = 50;

        Assert.Equal(originalVersion, original.MathematicalVersion);
        Assert.Empty(originalRaised);
        Assert.True(clone.MathematicalVersion > originalVersion);
    }

    [Theory]
    [InlineData(nameof(INotifyPropertyChanged.PropertyChanged))]
    [InlineData("PropertyChanging")]
    public void CommunityToolkitObservableObject_StillExposesTheEventFieldsThatSettingsCloneResets(string eventName)
    {
        var field = typeof(CommunityToolkit.Mvvm.ComponentModel.ObservableObject).GetField(
            eventName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        Assert.True(field != null,
            $"ObservableObject no longer has a private '{eventName}' backing field: CoreIndicatorSettings.DetachInheritedSubscribers must be updated, " +
            "otherwise Clone()/Snapshot() copies would notify the original's subscribers again.");
    }

    [Fact]
    public void SettingsSnapshot_KeepsIdAndVersion_ButDoesNotInheritSubscribers()
    {
        var original = new CoreIndicatorSettings { ParameterObject = new CoreSmaParameter { Period = 20 }, MathematicalVersion = 3 };
        int originalRaised = 0;
        original.PropertyChanged += (_, _) => originalRaised++;

        var snapshot = original.Snapshot();
        ((CoreSmaParameter)snapshot.ParameterObject!).Period = 60;

        Assert.Equal(original.Id, snapshot.Id);
        Assert.Equal(0, originalRaised);
        Assert.Equal(20, ((CoreSmaParameter)original.ParameterObject!).Period);
    }
}
