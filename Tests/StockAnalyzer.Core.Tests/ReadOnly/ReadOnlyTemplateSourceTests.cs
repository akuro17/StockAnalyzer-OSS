using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Models.Templates;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;
using StockAnalyzer.TestSupport;
using Xunit;

namespace StockAnalyzer.Core.Tests.ReadOnly;

/// <summary>The read-only template source: it lists and reads Screener templates written the way TemplateService writes them, and changes nothing.</summary>
public sealed class ReadOnlyTemplateSourceTests : IDisposable
{
    private readonly string _root = TestDataIsolationBootstrap.NewTempDirectory("ReadOnlyTemplates");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* best-effort */ }
    }

    private string TemplateDirectory => Path.Combine(_root, "Templates", "Screener");

    private static ScreenerIndicatorTemplate Template(string name, bool favorite = false, decimal level = 100m, LogicalOperator join = LogicalOperator.And)
    {
        var template = new ScreenerIndicatorTemplate { Id = Guid.NewGuid(), Name = name, IsFavorite = favorite, Description = name + " description" };
        template.SetEntries(new[]
        {
            new ScreenerIndicatorEntry
            {
                LeftHand = new ScreenerIndicatorSideConfig { IndicatorType = IndicatorType.Price, OutputName = "Close", CustomDisplayName = "Close" },
                Operator = ComparisonOperator.GreaterThan,
                TargetMode = RightHandTargetMode.NumericValue,
                RightNumericValue = level,
                LogicalOperator = join,
            },
        });
        return template;
    }

    private void Write(ScreenerIndicatorTemplate template, string? fileName = null)
    {
        Directory.CreateDirectory(TemplateDirectory);
        File.WriteAllText(Path.Combine(TemplateDirectory, fileName ?? $"{template.Id}.json"), JsonSerializer.Serialize(template, FileReadOnlyTemplateSource.CreateJsonOptions()));
    }

    [Fact]
    public async Task AMissingFolderIsAnEmptyListingAndIsNotCreated()
    {
        var source = new FileReadOnlyTemplateSource(TemplateDirectory);
        var before = ReadOnlyDataTreeFixture.SnapshotOf(_root);

        ScreenerTemplateListing listing = await source.ListScreenerTemplatesAsync();

        Assert.Empty(listing.Templates);
        Assert.Equal(0, listing.UnreadableFileCount);
        Assert.Null(await source.GetScreenerTemplateAsync(Guid.NewGuid()));
        Assert.False(Directory.Exists(TemplateDirectory));
        Assert.Equal(before, ReadOnlyDataTreeFixture.SnapshotOf(_root));
    }

    [Fact]
    public async Task TemplatesAreListedFavouritesFirstThenByNameIgnoringCase()
    {
        Write(Template("beta"));
        Write(Template("Alpha"));
        Write(Template("zeta", favorite: true));

        ScreenerTemplateListing listing = await new FileReadOnlyTemplateSource(TemplateDirectory).ListScreenerTemplatesAsync();

        Assert.Equal(new[] { "zeta", "Alpha", "beta" }, listing.Templates.Select(t => t.Name).ToArray());
    }

    [Fact]
    public async Task AnEntryComesBackWithItsSideOperatorValueAndJoin()
    {
        ScreenerIndicatorTemplate saved = Template("one", level: 123.5m, join: LogicalOperator.Or);
        Write(saved);

        ScreenerIndicatorTemplate? loaded = await new FileReadOnlyTemplateSource(TemplateDirectory).GetScreenerTemplateAsync(saved.Id);

        Assert.NotNull(loaded);
        Assert.Equal(saved.Id, loaded.Id);
        Assert.Equal("one description", loaded.Description);
        ScreenerIndicatorEntry entry = Assert.Single(loaded.Entries);
        Assert.Equal(IndicatorType.Price, entry.LeftHand.IndicatorType);
        Assert.Equal("Close", entry.LeftHand.OutputName);
        Assert.Equal(ComparisonOperator.GreaterThan, entry.Operator);
        Assert.Equal(123.5m, entry.RightNumericValue);
        Assert.Equal(LogicalOperator.Or, entry.LogicalOperator);
    }

    [Fact]
    public async Task ATemplateIsFoundByTheHyphenatedIdAndByTheIdWithoutHyphens()
    {
        ScreenerIndicatorTemplate hyphenated = Template("hyphenated");
        ScreenerIndicatorTemplate compact = Template("compact");
        Write(hyphenated);
        Write(compact, $"{compact.Id:N}.json");
        var source = new FileReadOnlyTemplateSource(TemplateDirectory);

        Assert.Equal("hyphenated", (await source.GetScreenerTemplateAsync(hyphenated.Id))!.Name);
        Assert.Equal("compact", (await source.GetScreenerTemplateAsync(compact.Id))!.Name);
        Assert.Null(await source.GetScreenerTemplateAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task AnUnreadableFileIsCountedAndSkippedAndTheOthersAreStillListed()
    {
        Write(Template("good"));
        ScreenerIndicatorTemplate broken = Template("broken");
        File.WriteAllText(Path.Combine(TemplateDirectory, $"{broken.Id}.json"), "{ this is not json");
        File.WriteAllText(Path.Combine(TemplateDirectory, "notes.txt"), "not a template file");
        var source = new FileReadOnlyTemplateSource(TemplateDirectory);

        ScreenerTemplateListing listing = await source.ListScreenerTemplatesAsync();

        Assert.Equal(new[] { "good" }, listing.Templates.Select(t => t.Name).ToArray());
        Assert.Equal(1, listing.UnreadableFileCount);
        Assert.Null(await source.GetScreenerTemplateAsync(broken.Id));
    }

    [Fact]
    public async Task ReadingChangesNothingUnderTheFolder()
    {
        ScreenerIndicatorTemplate template = Template("kept");
        Write(template);
        File.WriteAllText(Path.Combine(TemplateDirectory, "broken.json"), "{");
        var before = ReadOnlyDataTreeFixture.SnapshotOf(_root);
        var source = new FileReadOnlyTemplateSource(TemplateDirectory);

        await source.ListScreenerTemplatesAsync();
        await source.GetScreenerTemplateAsync(template.Id);
        await source.GetScreenerTemplateAsync(Guid.NewGuid());

        Assert.Equal(before, ReadOnlyDataTreeFixture.SnapshotOf(_root));
    }

    [Fact]
    public async Task ACancelledTokenStopsTheListing()
    {
        Write(Template("a"));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new FileReadOnlyTemplateSource(TemplateDirectory).ListScreenerTemplatesAsync(cancelled.Token));
    }

    [Fact]
    public void TheJsonOptionsWriteWhatTemplateServiceWrites()
    {
        // TemplateService keeps its options private; a template written with them must be written identically by the options used to read here.
        using var service = new TemplateService();
        var serviceOptions = (JsonSerializerOptions)typeof(TemplateService).GetField("_jsonOptions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
        ScreenerIndicatorTemplate template = Template("same");

        string fromService = JsonSerializer.Serialize(template, serviceOptions);
        string fromSource = JsonSerializer.Serialize(template, FileReadOnlyTemplateSource.CreateJsonOptions());

        Assert.Equal(fromService, fromSource);
        Assert.Equal(serviceOptions.IncludeFields, FileReadOnlyTemplateSource.CreateJsonOptions().IncludeFields);
        Assert.Equal(serviceOptions.PropertyNameCaseInsensitive, FileReadOnlyTemplateSource.CreateJsonOptions().PropertyNameCaseInsensitive);
        Assert.Equal(serviceOptions.NumberHandling, FileReadOnlyTemplateSource.CreateJsonOptions().NumberHandling);
    }

    [Fact]
    public void TheTemplatesFolderResolverNamesTheCreatingResolversFolderAndCreatesNothing()
    {
        string bin = Path.Combine(_root, "bin", "net8.0");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(_root, "StockAnalyzer.sln"), string.Empty);
        var before = ReadOnlyDataTreeFixture.SnapshotOf(_root);

        string resolved = PathDiscovery.ResolveTemplatesDirectoryWithoutCreating(TemplateType.Screener, bin);

        Assert.Equal(Path.GetFullPath(Path.Combine(_root, "Data", "Templates", "Screener")), resolved);
        Assert.Equal(before, ReadOnlyDataTreeFixture.SnapshotOf(_root));

        string real = PathDiscovery.ResolveTemplatesDirectoryWithoutCreating(TemplateType.Screener);
        if (Directory.Exists(real))
        {
            Assert.Equal(PathDiscovery.ResolveTemplatesDirectory(TemplateType.Screener), real);
        }
    }
}
