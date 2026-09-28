using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Serialization;
using Xunit;

namespace StockAnalyzer.Tests.Drawing;

/// <summary>
/// T1 of the "SubWindow Drawing Tools" feature.
/// Every concrete <see cref="IChartObject"/> must expose a real
/// <c>PanelIndex { get; set; }</c> (default <c>-1</c> = Main Chart) rather than fall back to the
/// interface's default no-op member (<c>get =&gt; -1; set { }</c>), which silently discards any
/// panel assignment. This mirrors <see cref="ChartObjectAnchorPointComplianceTests"/> and
/// SA_ARCHITECTURE_RULES.md "Explicit Interface Property Implementation for Polymorphic Hierarchies".
/// </summary>
public class SubWindowDrawingTests
{
    private static IEnumerable<Type> ConcreteChartObjectTypes() =>
        typeof(IChartObject).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(IChartObject).IsAssignableFrom(t));

    private static readonly JsonSerializerOptions Options = new()
    {
        Converters =
        {
            new ChartObjectJsonConverter(),
            new AvaloniaColorJsonConverter(),
            new JsonStringEnumConverter()
        }
    };

    [Fact]
    public void AllConcreteChartObjects_DeclarePanelIndexExplicitly()
    {
        var violations = ConcreteChartObjectTypes()
            .Where(t =>
            {
                var property = t.GetProperty(nameof(IChartObject.PanelIndex));
                return property is null || property.DeclaringType == typeof(IChartObject);
            })
            .Select(t => t.FullName)
            .OrderBy(name => name)
            .ToList();

        Assert.True(violations.Count == 0,
            "The following IChartObject implementers rely on the DIM no-op PanelIndex default " +
            "instead of declaring their own { get; set; } auto-property: " + string.Join(", ", violations));
    }

    [Fact]
    public void PanelIndex_DefaultsToMinusOne_ForEveryInstantiableConcreteType()
    {
        var exercised = 0;
        var wrong = new List<string>();

        foreach (var type in ConcreteChartObjectTypes())
        {
            IChartObject obj;
            try { obj = ChartObjectInstanceFactory.Create(type); }
            catch { continue; } // instantiability is out of scope for T1; covered elsewhere

            exercised++;
            if (obj.PanelIndex != -1) wrong.Add($"{type.Name}={obj.PanelIndex}");
        }

        Assert.True(exercised > 20, $"Sanity: expected to exercise many types, only hit {exercised}.");
        Assert.True(wrong.Count == 0, "Concrete types with a non -1 default PanelIndex: " + string.Join(", ", wrong));
    }

    [Fact]
    public void PanelIndex_RoundTripsThroughJson_ForEveryInstantiableConcreteType()
    {
        var exercised = 0;
        var failures = new List<string>();

        foreach (var type in ConcreteChartObjectTypes())
        {
            IChartObject original;
            try { original = ChartObjectInstanceFactory.Create(type); }
            catch { continue; }

            original.PanelIndex = 2;

            string json;
            IChartObject? restored;
            try
            {
                json = JsonSerializer.Serialize<IChartObject>(original, Options);
                restored = JsonSerializer.Deserialize<IChartObject>(json, Options);
            }
            catch
            {
                // A few types have pre-existing generic-serialization limitations unrelated to
                // PanelIndex (e.g. HUD-only InformationObject, analytical projection objects).
                // T1 only guarantees PanelIndex is preserved wherever serialization already works.
                continue;
            }

            exercised++;
            if (restored is null || restored.PanelIndex != 2)
                failures.Add($"{type.Name} -> {(restored is null ? "null" : restored.PanelIndex.ToString())}");
        }

        Assert.True(exercised > 20, $"Sanity: expected to round-trip many types, only hit {exercised}.");
        Assert.True(failures.Count == 0, "PanelIndex did not round-trip for: " + string.Join(", ", failures));
    }

    [Fact]
    public void LegacyJsonWithoutPanelIndex_DeserializesAsMainChartMinusOne()
    {
        var hLine = new HorizontalLineObject(new ChartPoint(new DateTime(2024, 1, 1), 150m))
        {
            PanelIndex = 3
        };
        var json = JsonSerializer.Serialize<IChartObject>(hLine, Options);

        using (var doc = JsonDocument.Parse(json))
        {
            Assert.True(doc.RootElement.TryGetProperty("panelIndex", out _),
                "Precondition: a migrated type should serialize a panelIndex property.");
        }

        // Simulate a workspace saved before PanelIndex existed: no "panelIndex" key at all.
        var legacy = Regex.Replace(json, "\"panelIndex\"\\s*:\\s*-?\\d+\\s*,?", "");
        legacy = legacy.Replace(",}", "}").Replace("{,", "{");

        var restored = JsonSerializer.Deserialize<IChartObject>(legacy, Options);

        Assert.NotNull(restored);
        Assert.IsType<HorizontalLineObject>(restored);
        Assert.Equal(-1, restored!.PanelIndex);
    }
}
