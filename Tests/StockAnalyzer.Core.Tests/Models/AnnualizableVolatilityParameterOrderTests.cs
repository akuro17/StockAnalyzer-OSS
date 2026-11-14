using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Serialization;
using Xunit;

namespace StockAnalyzer.Core.Tests.Models;

/// <summary>
/// The Annualize and PeriodsPerYear properties live in <see cref="CoreAnnualizableVolatilityParameterBase"/>. Moving them there must not change what a consumer sees:
/// the reflection order of the properties (the settings dialog, the generated indicator catalog) and the serialized JSON (saved layouts, the backtest run fingerprint
/// serializes the parameter object with the workspace resolver).
/// </summary>
public class AnnualizableVolatilityParameterOrderTests
{
    private static string[] PropertyNames(System.Type type) =>
        type.GetProperties().Select(p => p.Name).Where(name => name != nameof(CoreIndicatorParameterBase.HasErrors)).ToArray();

    [Fact]
    public void GarmanKlass_KeepsTheOrderPeriodAnnualizePeriodsPerYear()
    {
        Assert.Equal(new[] { "Period", "Annualize", "PeriodsPerYear" }, PropertyNames(typeof(CoreGarmanKlassVolatilityParameter)));
    }

    [Theory]
    [InlineData(typeof(CoreParkinsonVolatilityParameter))]
    [InlineData(typeof(CoreRogersSatchellVolatilityParameter))]
    public void RangeVolatility_HasTheSameOrder(System.Type type)
    {
        Assert.Equal(new[] { "Period", "Annualize", "PeriodsPerYear" }, PropertyNames(type));
    }

    [Fact]
    public void Garch11_PutsTheAnnualizationAfterItsOwnProperties()
    {
        Assert.Equal(new[] { "Period", "RefitInterval", "MaxIterations", "Annualize", "PeriodsPerYear" }, PropertyNames(typeof(CoreGarch11Parameter)));
    }

    private static string DescriptionOf(System.Type type, string propertyName) =>
        type.GetProperty(propertyName)!.GetCustomAttribute<DescriptionAttribute>()!.Description;

    // The tooltip is shared by indicators whose per-bar unit differs (Garman-Klass: ratio; range estimators and GARCH(1,1): percent), so it must not name a unit.
    [Theory]
    [InlineData(typeof(CoreGarmanKlassVolatilityParameter))]
    [InlineData(typeof(CoreParkinsonVolatilityParameter))]
    [InlineData(typeof(CoreRogersSatchellVolatilityParameter))]
    [InlineData(typeof(CoreGarch11Parameter))]
    public void AnnualizeTooltip_DescribesThePerBarValue_WithoutNamingAUnit(System.Type type)
    {
        string description = DescriptionOf(type, nameof(CoreAnnualizableVolatilityParameterBase.Annualize));

        Assert.Equal(DescriptionOf(typeof(CoreAnnualizableVolatilityParameterBase), nameof(CoreAnnualizableVolatilityParameterBase.Annualize)), description);
        Assert.Contains("the value per bar", description);
        Assert.Contains("square root of Periods Per Year", description);
        Assert.DoesNotContain("ratio", description, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("percent", description, System.StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(typeof(CoreGarmanKlassVolatilityParameter))]
    [InlineData(typeof(CoreParkinsonVolatilityParameter))]
    [InlineData(typeof(CoreRogersSatchellVolatilityParameter))]
    [InlineData(typeof(CoreGarch11Parameter))]
    public void PeriodsPerYearTooltip_NamesTheBarsPerYearOfEachTimeFrame(System.Type type)
    {
        string description = DescriptionOf(type, nameof(CoreAnnualizableVolatilityParameterBase.PeriodsPerYear));

        Assert.Contains("252 daily, 52 weekly, 12 monthly", description);
        Assert.Contains("only when Annualize is on", description);
    }

    [Fact]
    public void GarmanKlass_SerializesExactlyAsBeforeTheBaseClass()
    {
        // The text of this JSON was captured from the class before its two properties moved to the shared base class.
        var options = new JsonSerializerOptions { TypeInfoResolver = WorkspacePolymorphicResolver.CreateResolver() };
        var parameter = new CoreGarmanKlassVolatilityParameter { Period = 5, Annualize = true, PeriodsPerYear = 52 };

        string json = JsonSerializer.Serialize(parameter, typeof(CoreIndicatorParameterBase), options);

        Assert.Equal("{\"$type\":\"CoreGarmanKlassVolatilityParameter\",\"Period\":5,\"Annualize\":true,\"PeriodsPerYear\":52}", json);
    }

    [Fact]
    public void GarmanKlass_StillRejectsAnOutOfRangePeriodsPerYear_NamingTheProperty()
    {
        var ex = Assert.Throws<System.ArgumentOutOfRangeException>(() => new CoreGarmanKlassVolatilityParameter { PeriodsPerYear = 367 }.Validate());

        Assert.Equal(nameof(CoreGarmanKlassVolatilityParameter.PeriodsPerYear), ex.ParamName);
    }
}
