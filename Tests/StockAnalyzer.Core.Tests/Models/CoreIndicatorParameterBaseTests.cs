using System.ComponentModel;
using System.Linq;
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
}
