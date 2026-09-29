using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Parameters;
using Xunit;

namespace StockAnalyzer.Core.Tests.Models;

public class CoreVolumeProfileParameterTests
{
    // F04 regression: Value Area bins must default to yellow (#FFEB3B), not null/unspecified, whether
    // the parameter is freshly constructed or restored with an explicit null (an older saved settings
    // file predating this default). See
    // sa_analysis_report_VolumeProfile_PostImplementation_StrictReview_20260914.md F04/A09.

    [Fact]
    public void NewParameter_DefaultsValueAreaColorToYellow()
    {
        var param = new CoreVolumeProfileParameter();

        Assert.Equal(IndicatorDefaultConstants.VolumeProfileValueAreaColor, param.ValueAreaColor);
        Assert.Equal(IndicatorDefaultConstants.VolumeProfileValueAreaColor, param.EffectiveValueAreaColor);
    }

    [Fact]
    public void ExplicitNullValueAreaColor_ResolvesEffectiveColorToYellow()
    {
        // Simulates restoring an older saved settings file that persisted an explicit null.
        var param = new CoreVolumeProfileParameter { ValueAreaColor = null };

        Assert.Null(param.ValueAreaColor);
        Assert.Equal(IndicatorDefaultConstants.VolumeProfileValueAreaColor, param.EffectiveValueAreaColor);
    }

    [Fact]
    public void ExplicitCustomValueAreaColor_IsPreservedAndNotOverwritten()
    {
        var customColor = new IndicatorColor(255, 10, 20, 30);
        var param = new CoreVolumeProfileParameter { ValueAreaColor = customColor };

        Assert.Equal(customColor, param.ValueAreaColor);
        Assert.Equal(customColor, param.EffectiveValueAreaColor);
    }
}
