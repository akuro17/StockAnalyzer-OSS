using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Models.Templates;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Tests.TestHelpers;
using StockAnalyzer.TestSupport;
using Xunit;
using static StockAnalyzer.Core.Tests.TestHelpers.ScreenerSideFrameFixtures;

namespace StockAnalyzer.Core.Tests;

/// <summary>
/// Schema 2 of <see cref="ScreenerIndicatorTemplate"/> (the time frame of a side is read by the Screener): a template of schema 1 that has a weekly or monthly bar-reading
/// side is discarded as a whole, every other schema-1 template is migrated to schema 2 unchanged, and the readers (TemplateService, the read-only source) do not hand a
/// discarded template out. The mechanism (IJsonOnDeserialized) is checked with BOTH option sets the readers use.
/// </summary>
public sealed class ScreenerIndicatorTemplateSchemaTests : IDisposable
{
    private readonly string _root = TestDataIsolationBootstrap.NewTempDirectory("ScreenerTemplateSchema");
    private readonly TemplateService _service = new();

    public void Dispose()
    {
        _service.Dispose();
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* best-effort */ }
    }

    private static ScreenerIndicatorTemplate Template(string name, params ScreenerIndicatorEntry[] entries)
    {
        var template = new ScreenerIndicatorTemplate { Id = Guid.NewGuid(), Name = name };
        template.SetEntries(entries);
        return template;
    }

    private static readonly Regex SchemaField = new("\"SchemaVersion\"\\s*:\\s*\\d+", RegexOptions.Compiled);

    /// <summary>The JSON a TemplateService of the older version wrote: the same text with schema 1.</summary>
    private static string LegacyJson(ScreenerIndicatorTemplate template, JsonSerializerOptions options)
        => SchemaField.Replace(JsonSerializer.Serialize(template, options), "\"SchemaVersion\": 1");

    private static JsonSerializerOptions ServiceOptions(TemplateService service)
        => (JsonSerializerOptions)typeof(TemplateService).GetField("_jsonOptions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;

    private static IEnumerable<object[]> OptionSets()
    {
        yield return new object[] { "TemplateService", true };
        yield return new object[] { "ReadOnlySource", false };
    }

    private JsonSerializerOptions Options(bool service) => service ? ServiceOptions(_service) : FileReadOnlyTemplateSource.CreateJsonOptions();

    // ---- the schema number ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ANewTemplateIsOfTheCurrentSchemaAndWritesIt()
    {
        ScreenerIndicatorTemplate template = Template("new");

        Assert.Equal(ScreenerIndicatorTemplate.CurrentSchemaVersion, template.SchemaVersion);
        Assert.Equal(2, ScreenerIndicatorTemplate.CurrentSchemaVersion);
        Assert.Matches("\"SchemaVersion\":\\s*2", JsonSerializer.Serialize(template, FileReadOnlyTemplateSource.CreateJsonOptions()));
        Assert.False(template.IsDiscardedLegacy);
    }

    [Fact]
    public void EveryOtherTemplateTypeIsNeverDiscarded()
        => Assert.False(new IndicatorTemplate().IsDiscardedLegacy);

    // ---- the migration rule, with both option sets -----------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(OptionSets))]
    public void AVersion2TemplateWithAWeeklySideIsKept(string _, bool service)
    {
        ScreenerIndicatorTemplate written = Template("weekly", Versus(Close(TimeFrame.W1), ComparisonOperator.GreaterThan, 1m));

        var read = JsonSerializer.Deserialize<ScreenerIndicatorTemplate>(JsonSerializer.Serialize(written, Options(service)), Options(service))!;

        Assert.False(read.IsDiscardedLegacy);
        Assert.Equal(2, read.SchemaVersion);
        Assert.Equal(TimeFrame.W1, Assert.Single(read.Entries).LeftHand.TimeFrame);
    }

    [Theory]
    [MemberData(nameof(OptionSets))]
    public void AVersion1TemplateWithADailyOnlyContentIsMigratedUnchanged(string _, bool service)
    {
        ScreenerIndicatorTemplate written = Template("daily", Versus(Close(), ComparisonOperator.GreaterThan, Sma(5)), Versus(Fundamental(TimeFrame.MN1), ComparisonOperator.GreaterThan, 1m));

        var read = JsonSerializer.Deserialize<ScreenerIndicatorTemplate>(LegacyJson(written, Options(service)), Options(service))!;

        Assert.False(read.IsDiscardedLegacy);
        Assert.Equal(2, read.SchemaVersion);
        Assert.Equal(2, read.Entries.Count);
    }

    [Theory]
    [MemberData(nameof(OptionSets))]
    public void AVersion1TemplateWithAWeeklyOrMonthlyBarReadingSideIsDiscardedAsAWhole(string _, bool service)
    {
        foreach (ScreenerIndicatorEntry legacy in new[]
                 {
                     Versus(Sma(5, TimeFrame.W1), ComparisonOperator.GreaterThan, 1m),                              // an indicator
                     Versus(Close(TimeFrame.MN1), ComparisonOperator.GreaterThan, 1m),                              // a price (the old engine read it on daily bars)
                     Versus(CloseColumn(TimeFrame.W1), ComparisonOperator.GreaterThan, 1m),                         // a Column that names a bar value
                     Versus(Close(), ComparisonOperator.GreaterThan, Sma(5, TimeFrame.W1)),                         // the right side of an indicator comparison
                     Versus(Sma(5, TimeFrame.W1), ComparisonOperator.GreaterThan, 1m, enabled: false),              // a disabled entry counts too
                 })
        {
            ScreenerIndicatorTemplate written = Template("legacy", Versus(Close(), ComparisonOperator.GreaterThan, 1m), legacy);

            var read = JsonSerializer.Deserialize<ScreenerIndicatorTemplate>(LegacyJson(written, Options(service)), Options(service))!;

            Assert.True(read.IsDiscardedLegacy);
        }
    }

    [Theory]
    [MemberData(nameof(OptionSets))]
    public void AFrameThatTheOldEngineNeverUsedDoesNotDiscardAVersion1Template(string _, bool service)
    {
        ScreenerIndicatorEntry numericTarget = Versus(Close(), ComparisonOperator.GreaterThan, 1m);
        numericTarget.RightHand = Sma(5, TimeFrame.W1); // stored, not evaluated in the numeric target mode

        ScreenerIndicatorTemplate written = Template("ignored", numericTarget, Versus(Fundamental(TimeFrame.W1), ComparisonOperator.GreaterThan, 1m));

        var read = JsonSerializer.Deserialize<ScreenerIndicatorTemplate>(LegacyJson(written, Options(service)), Options(service))!;

        Assert.False(read.IsDiscardedLegacy);
        Assert.Equal(2, read.SchemaVersion);
    }

    [Fact]
    public void AFileWithoutASchemaNumberIsReadAsTheCurrentSchema()
    {
        ScreenerIndicatorTemplate written = Template("no number", Versus(Close(TimeFrame.W1), ComparisonOperator.GreaterThan, 1m));
        string json = SchemaField.Replace(JsonSerializer.Serialize(written, FileReadOnlyTemplateSource.CreateJsonOptions()), "\"Unrelated\": 0");

        var read = JsonSerializer.Deserialize<ScreenerIndicatorTemplate>(json, FileReadOnlyTemplateSource.CreateJsonOptions())!;

        Assert.False(read.IsDiscardedLegacy);
        Assert.Equal(2, read.SchemaVersion);
    }

    // ---- the readers -------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TemplateServiceDoesNotHandOutADiscardedTemplateButHandsOutAMigratedOne()
    {
        ScreenerIndicatorTemplate legacy = Template("legacy weekly", Versus(Sma(5, TimeFrame.W1), ComparisonOperator.GreaterThan, 1m));
        ScreenerIndicatorTemplate daily = Template("legacy daily", Versus(Close(), ComparisonOperator.GreaterThan, 1m));
        ScreenerIndicatorTemplate current = Template("current weekly", Versus(Sma(5, TimeFrame.W1), ComparisonOperator.GreaterThan, 1m));
        foreach (ScreenerIndicatorTemplate template in new[] { legacy, daily, current })
        {
            await _service.SaveAsync(template);
        }

        Downgrade(legacy);
        Downgrade(daily);
        try
        {
            Assert.Null(await _service.GetAsync<ScreenerIndicatorTemplate>(TemplateType.Screener, legacy.Id));
            ScreenerIndicatorTemplate? migrated = await _service.GetAsync<ScreenerIndicatorTemplate>(TemplateType.Screener, daily.Id);
            Assert.NotNull(migrated);
            Assert.Equal(2, migrated!.SchemaVersion);
            Assert.NotNull(await _service.GetAsync<ScreenerIndicatorTemplate>(TemplateType.Screener, current.Id));

            IReadOnlyList<ScreenerIndicatorTemplate> all = await _service.GetAllAsync<ScreenerIndicatorTemplate>(TemplateType.Screener);
            Assert.DoesNotContain(all, template => template.Id == legacy.Id);
            Assert.Contains(all, template => template.Id == daily.Id);
            Assert.Contains(all, template => template.Id == current.Id);
            Assert.True(File.Exists(PathDiscovery.ResolveTemplatePath(TemplateType.Screener, $"{legacy.Id}.json"))); // reading never deletes the file
        }
        finally
        {
            foreach (ScreenerIndicatorTemplate template in new[] { legacy, daily, current })
            {
                await _service.DeleteAsync(TemplateType.Screener, template.Id);
            }
        }
    }

    private static void Downgrade(ScreenerIndicatorTemplate template)
    {
        string path = PathDiscovery.ResolveTemplatePath(TemplateType.Screener, $"{template.Id}.json");
        File.WriteAllText(path, SchemaField.Replace(File.ReadAllText(path), "\"SchemaVersion\": 1"));
    }

    [Fact]
    public async Task TheReadOnlySourceCountsADiscardedTemplateAsUnreadableAndGetsNone()
    {
        string directory = Path.Combine(_root, "Templates", "Screener");
        Directory.CreateDirectory(directory);
        ScreenerIndicatorTemplate legacy = Template("legacy weekly", Versus(Close(TimeFrame.W1), ComparisonOperator.GreaterThan, 1m));
        ScreenerIndicatorTemplate daily = Template("legacy daily", Versus(Close(), ComparisonOperator.GreaterThan, 1m));
        JsonSerializerOptions options = FileReadOnlyTemplateSource.CreateJsonOptions();
        File.WriteAllText(Path.Combine(directory, $"{legacy.Id}.json"), LegacyJson(legacy, options));
        File.WriteAllText(Path.Combine(directory, $"{daily.Id}.json"), LegacyJson(daily, options));
        var source = new FileReadOnlyTemplateSource(directory);

        ScreenerTemplateListing listing = await source.ListScreenerTemplatesAsync();

        Assert.Equal(new[] { daily.Id }, listing.Templates.Select(template => template.Id));
        Assert.Equal(1, listing.UnreadableFileCount);
        Assert.Null(await source.GetScreenerTemplateAsync(legacy.Id));
        Assert.NotNull(await source.GetScreenerTemplateAsync(daily.Id));
    }
}
