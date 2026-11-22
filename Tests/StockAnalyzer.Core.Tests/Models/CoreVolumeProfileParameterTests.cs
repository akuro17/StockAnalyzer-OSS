using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Parameters;
using Xunit;

namespace StockAnalyzer.Core.Tests.Models;

public class CoreVolumeProfileParameterTests
{
    // The options the workspace files use: every concrete parameter class is registered for polymorphic JSON by WorkspacePolymorphicResolver.
    private static readonly JsonSerializerOptions WorkspaceOptions = new() { TypeInfoResolver = StockAnalyzer.Core.Serialization.WorkspacePolymorphicResolver.CreateResolver() };

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

    // POC colour: the Point of Control bin defaults to red (#FF0000) from ONE constant; unset, null and an older file without the key all resolve to it.

    [Fact]
    public void NewParameter_DefaultsPocColorToTheRedConstant()
    {
        var vp = new CoreVolumeProfileParameter();
        var tap = new CoreTimeAtPriceParameter();

        Assert.Equal(IndicatorDefaultConstants.VolumeProfilePocColor, vp.PocColor);
        Assert.Equal(IndicatorDefaultConstants.VolumeProfilePocColor, vp.EffectivePocColor);
        Assert.Equal(IndicatorDefaultConstants.VolumeProfilePocColor, tap.PocColor);
        Assert.Equal(IndicatorDefaultConstants.VolumeProfilePocColor, tap.EffectivePocColor);
        Assert.Equal(new IndicatorColor(255, 255, 0, 0), IndicatorDefaultConstants.VolumeProfilePocColor);
    }

    [Fact]
    public void ThemeColorsPocColor_IsTheSameSingleDefinition()
    {
        Assert.Equal(IndicatorDefaultConstants.VolumeProfilePocColor, StockAnalyzer.Core.Theme.ThemeColors.Light.VolumeProfilePOC);
        Assert.Equal(IndicatorDefaultConstants.VolumeProfilePocColor, StockAnalyzer.Core.Theme.ThemeColors.Dark.VolumeProfilePOC);
        Assert.Equal(new IndicatorColor(255, 255, 0, 0), StockAnalyzer.Core.Theme.ThemeColors.Light.VolumeProfilePOC);
    }

    [Fact]
    public void ExplicitNullPocColor_ResolvesEffectiveColorToRed_ForBothParameters()
    {
        var vp = new CoreVolumeProfileParameter { PocColor = null };
        var tap = new CoreTimeAtPriceParameter { PocColor = null };

        Assert.Null(vp.PocColor);
        Assert.Equal(IndicatorDefaultConstants.VolumeProfilePocColor, vp.EffectivePocColor);
        Assert.Null(tap.PocColor);
        Assert.Equal(IndicatorDefaultConstants.VolumeProfilePocColor, tap.EffectivePocColor);
    }

    [Fact]
    public void ExplicitCustomPocColor_IsPreservedAndNotOverwritten_ForBothParameters()
    {
        var customColor = new IndicatorColor(255, 10, 20, 30);
        var vp = new CoreVolumeProfileParameter { PocColor = customColor };
        var tap = new CoreTimeAtPriceParameter { PocColor = customColor };

        Assert.Equal(customColor, vp.PocColor);
        Assert.Equal(customColor, vp.EffectivePocColor);
        Assert.Equal(customColor, tap.PocColor);
        Assert.Equal(customColor, tap.EffectivePocColor);
    }

    [Fact]
    public void PocColor_ChangeRaisesPropertyChanged_AndCloneCopiesIt()
    {
        var param = new CoreVolumeProfileParameter();
        var raised = new System.Collections.Generic.List<string?>();
        param.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        var customColor = new IndicatorColor(255, 1, 2, 3);
        param.PocColor = customColor;

        Assert.Contains(nameof(CoreVolumeProfileParameter.PocColor), raised);
        Assert.Equal(customColor, ((CoreVolumeProfileParameter)param.Clone()).PocColor);
    }

    [Fact]
    public void PocColor_DoesNotChangeTheValueAreaColor()
    {
        var param = new CoreVolumeProfileParameter { PocColor = new IndicatorColor(255, 1, 2, 3) };

        Assert.Equal(IndicatorDefaultConstants.VolumeProfileValueAreaColor, param.ValueAreaColor);
        Assert.Equal(IndicatorDefaultConstants.VolumeProfileValueAreaColor, param.EffectiveValueAreaColor);
    }

    [Theory]
    [InlineData(false)] // the key is missing (a file saved before this setting existed)
    [InlineData(true)]  // the key is present as an explicit null
    public void SavedParameterWithoutAPocColor_LoadsWithTheRedDefault(bool explicitNull)
    {
        var originals = new CoreIndicatorParameterBase[]
        {
            new CoreVolumeProfileParameter { PocColor = new IndicatorColor(255, 1, 2, 3), Period = 77 },
            new CoreTimeAtPriceParameter { PocColor = new IndicatorColor(255, 1, 2, 3), Period = 77 }
        };

        foreach (CoreIndicatorParameterBase original in originals)
        {
            JsonObject obj = JsonSerializer.SerializeToNode(original, typeof(CoreIndicatorParameterBase), WorkspaceOptions)!.AsObject();
            string key = obj.First(kv => string.Equals(kv.Key, nameof(CoreVolumeProfileParameter.PocColor), System.StringComparison.OrdinalIgnoreCase)).Key;
            if (explicitNull) obj[key] = null; else obj.Remove(key);

            var loaded = JsonSerializer.Deserialize<CoreIndicatorParameterBase>(obj.ToJsonString(), WorkspaceOptions)!;

            (IndicatorColor effective, int period) = loaded switch
            {
                CoreVolumeProfileParameter vp => (vp.EffectivePocColor, vp.Period),
                CoreTimeAtPriceParameter tap => (tap.EffectivePocColor, tap.Period),
                _ => throw new Xunit.Sdk.XunitException($"unexpected type {loaded.GetType().Name}")
            };
            Assert.Equal(IndicatorDefaultConstants.VolumeProfilePocColor, effective);
            Assert.Equal(77, period); // the other saved values are restored unchanged
        }
    }

    [Fact]
    public void SavedParameterWithACustomPocColor_RoundTripsIt()
    {
        var custom = new IndicatorColor(255, 9, 8, 7);
        var json = JsonSerializer.Serialize<CoreIndicatorParameterBase>(new CoreVolumeProfileParameter { PocColor = custom }, WorkspaceOptions);

        var loaded = (CoreVolumeProfileParameter)JsonSerializer.Deserialize<CoreIndicatorParameterBase>(json, WorkspaceOptions)!;

        Assert.Equal(custom, loaded.PocColor);
    }
}
