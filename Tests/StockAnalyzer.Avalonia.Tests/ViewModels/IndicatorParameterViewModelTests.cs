using System.Globalization;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

public class IndicatorParameterViewModelTests
{
    [Fact]
    public void Validate_ShouldAcceptValidNumbers_InCurrentCulture()
    {
        // Arrange
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US"); // Uses dot decimal
            var vm = new IndicatorParameterViewModel
            {
                Name = "Test",
                ParameterType = typeof(double),
                Value = "1.5"
            };

            // Act
            bool isValid = vm.Validate();

            // Assert
            Assert.True(isValid);
            Assert.Null(vm.ValidationError);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public void Validate_ShouldAcceptValidNumbers_InCommaCulture()
    {
        // Arrange
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE"); // Uses comma decimal
            var vm = new IndicatorParameterViewModel
            {
                Name = "Test",
                ParameterType = typeof(double),
                Value = "1,5"
            };

            // Act
            bool isValid = vm.Validate();

            // Assert
            Assert.True(isValid);
            Assert.Null(vm.ValidationError);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public void Validate_ShouldRejectInvalidNumbers()
    {
        // Arrange
        var vm = new IndicatorParameterViewModel
        {
            Name = "Test",
            ParameterType = typeof(double),
            Value = "abc"
        };

        // Act
        bool isValid = vm.Validate();

        // Assert
        Assert.False(isValid);
        Assert.Equal("Please enter a numeric value", vm.ValidationError);
    }

    [Fact]
    public void Validate_ShouldEnforceMinMax()
    {
        // Arrange
        var vm = new IndicatorParameterViewModel
        {
            Name = "Test",
            ParameterType = typeof(double),
            Value = "10",
            MinValue = 0.0,
            MaxValue = 5.0
        };

        // Act
        bool isValid = vm.Validate();

        // Assert
        Assert.False(isValid);
        Assert.Contains("Maximum: 5", vm.ValidationError);
    }

    [Fact]
    public void Validate_ShouldHandleDecimalType()
    {
        // Arrange
        var vm = new IndicatorParameterViewModel
        {
            Name = "Test",
            ParameterType = typeof(decimal),
            Value = "2.5",
            MinValue = 0.0m,
            MaxValue = 5.0m
        };

        // Act
        bool isValid = vm.Validate();

        // Assert
        Assert.True(isValid);
    }
}
