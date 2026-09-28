using System.Linq;
using StockAnalyzer.Avalonia.Drawing;
using Xunit;

namespace StockAnalyzer.Tests.Drawing;

public class ChartObjectAnchorPointComplianceTests
{
    // SA_ARCHITECTURE_RULES.md "Explicit Interface Property Implementation for Polymorphic
    // Hierarchies": every concrete IChartObject MUST declare its own AnchorPointIndex
    // { get; set; } rather than silently relying on the interface's DIM no-op default
    // (get => 0; set { }), which discards any value assigned to it.
    [Fact]
    public void AllConcreteChartObjects_DeclareAnchorPointIndexExplicitly()
    {
        var concreteTypes = typeof(IChartObject).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(IChartObject).IsAssignableFrom(t));

        var violations = concreteTypes
            .Where(t =>
            {
                var property = t.GetProperty(nameof(IChartObject.AnchorPointIndex));
                return property is null || property.DeclaringType == typeof(IChartObject);
            })
            .Select(t => t.FullName)
            .OrderBy(name => name)
            .ToList();

        Assert.True(violations.Count == 0,
            "The following IChartObject implementers rely on the DIM no-op AnchorPointIndex default " +
            "instead of declaring their own { get; set; } auto-property: " + string.Join(", ", violations));
    }
}
