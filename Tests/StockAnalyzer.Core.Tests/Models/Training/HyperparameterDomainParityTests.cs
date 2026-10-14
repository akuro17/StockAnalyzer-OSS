using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using StockAnalyzer.Core.Models.Training;
using Xunit;

namespace StockAnalyzer.Core.Tests.Models.Training;

/// <summary>
/// <c>StockAnalyzer.Python/training/hyperparameter_domains.json</c> is the shared, machine-readable definition of the hyperparameter value domains; the Python
/// side (<c>hyperparameter_domains.py --selfcheck</c>) evaluates its validator on the same file. These tests hold the C# catalog to that file: every key, every
/// bound and every probe, so a domain changed on one side only fails here.
/// </summary>
public sealed class HyperparameterDomainParityTests
{
    private const string TrainingFolder = "training";

    private static string TrainingFile(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "StockAnalyzer.sln"))) directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, "StockAnalyzer.Python", TrainingFolder, name);
    }

    private static JsonElement Domains()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(TrainingFile("hyperparameter_domains.json")));
        return document.RootElement.GetProperty("domains").Clone();
    }

    private static double? Bound(JsonElement domain, string name) =>
        domain.GetProperty(name) is { ValueKind: not JsonValueKind.Null } value ? value.GetDouble() : null;

    [Fact]
    public void TheSharedFileAndTheCatalogHaveTheSameKeys()
    {
        var shared = Domains().EnumerateObject().Select(p => p.Name).OrderBy(k => k, StringComparer.Ordinal).ToArray();
        var catalog = TrainingHyperparameterCatalog.All.Where(d => d.Domain is not null).Select(d => d.Key).Distinct().OrderBy(k => k, StringComparer.Ordinal).ToArray();

        Assert.Equal(catalog, shared);
    }

    [Fact]
    public void EveryDomainOfTheCatalogEqualsTheSharedDefinition()
    {
        var mismatches = new List<string>();
        foreach (var entry in Domains().EnumerateObject())
        {
            var domain = TrainingHyperparameterCatalog.Find(entry.Name)!.Domain!;
            var kind = entry.Value.GetProperty("kind").GetString() == "integer" ? HyperparameterKind.Integer : HyperparameterKind.Real;
            if (domain.Kind != kind) mismatches.Add($"{entry.Name}: kind {domain.Kind} != {kind}");
            if (domain.Minimum != Bound(entry.Value, "minimum")) mismatches.Add($"{entry.Name}: minimum {domain.Minimum} != {Bound(entry.Value, "minimum")}");
            if (domain.Maximum != Bound(entry.Value, "maximum")) mismatches.Add($"{entry.Name}: maximum {domain.Maximum} != {Bound(entry.Value, "maximum")}");
            if (domain.MinimumInclusive != entry.Value.GetProperty("minimum_inclusive").GetBoolean()) mismatches.Add($"{entry.Name}: minimum_inclusive");
            if (domain.MaximumInclusive != entry.Value.GetProperty("maximum_inclusive").GetBoolean()) mismatches.Add($"{entry.Name}: maximum_inclusive");
        }

        Assert.Empty(mismatches);
    }

    [Fact]
    public void EveryProbeOfTheSharedDefinitionGivesTheSameProblemInCSharp()
    {
        var mismatches = new List<string>();
        int probes = 0;
        foreach (var entry in Domains().EnumerateObject())
        {
            Assert.NotEmpty(entry.Value.GetProperty("probes").EnumerateArray());
            foreach (var probe in entry.Value.GetProperty("probes").EnumerateArray())
            {
                probes++;
                string value = probe[0].GetString()!, expected = probe[1].GetString()!;
                var actual = TrainingHyperparameterCatalog.Validate(entry.Name, value).ToString();
                if (actual != expected) mismatches.Add($"{entry.Name} '{value}': Python {expected}, C# {actual}");
            }
        }

        Assert.True(probes > 0);
        Assert.Empty(mismatches);
    }

    [Fact]
    public void TheTrainerRulesAndConstantsTheDefinitionQuotesStillExist()
    {
        foreach (var entry in Domains().EnumerateObject())
        {
            if (entry.Value.TryGetProperty("trainer_checks", out var checks))
                foreach (var check in checks.EnumerateArray())
                    Assert.Contains(check.GetProperty("text").GetString()!, File.ReadAllText(TrainingFile(check.GetProperty("file").GetString()!)));
            if (entry.Value.TryGetProperty("bound_constants", out var constants))
                foreach (var constant in constants.EnumerateArray())
                {
                    string name = constant.GetProperty("name").GetString()!;
                    var source = File.ReadAllText(TrainingFile(constant.GetProperty("file").GetString()!));
                    var match = System.Text.RegularExpressions.Regex.Match(source, $@"^{name}\s*:\s*\w+\s*=\s*(?<v>[-+0-9.eE]+)\s*(#.*)?$",
                        System.Text.RegularExpressions.RegexOptions.Multiline);
                    Assert.True(match.Success, $"{name} is no numeric literal any more");
                    Assert.Equal(double.Parse(match.Groups["v"].Value, CultureInfo.InvariantCulture), Bound(entry.Value, constant.GetProperty("bound").GetString()!));
                }
        }
    }

    [Fact]
    public void DefaultHorizon_MatchesPythonDatasetDefault()
    {
        var content = File.ReadAllText(TrainingFile("dataset.py"));
        var matches = System.Text.RegularExpressions.Regex.Matches(content, @"DEFAULT_HORIZON:\s*int\s*=\s*(\d+)");
        Assert.Single(matches);
        Assert.Equal(WalkForwardDataRequirement.DefaultHorizon, int.Parse(matches[0].Groups[1].Value, CultureInfo.InvariantCulture));
    }
}
