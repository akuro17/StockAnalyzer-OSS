using System.IO;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Catalog;
using StockAnalyzer.McpServer.Tools;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// MP-A (T2): guards that keep the MCP surface in step with the engine. A setting, comparison operator or indicator parameter type that is added
/// to the application and is neither offered nor deliberately left out fails here, instead of staying silently missing.
/// </summary>
public sealed class McpParityGuardTests : IDisposable
{
    private readonly string _emptyRoot = Path.Combine(Path.GetTempPath(), "SaMcpParityGuard-" + Guid.NewGuid().ToString("N"));
    private readonly List<McpServerRuntime> _runtimes = new();

    public void Dispose()
    {
        foreach (McpServerRuntime runtime in _runtimes)
        {
            runtime.Dispose();
        }
    }

    // ---- AT-A.1 settings exposure ------------------------------------------------------------------------------------

    [Fact]
    public void BacktestSettings_EverySettablePropertyIsAllowedOrOmittedExactlyOnce()
    {
        IReadOnlyList<string> problems = ExposureProblems(
            McpBacktestSettingsExposure.Owners,
            McpBacktestSettingsExposure.Allowed.Select(a => (a.Owner, a.Property)).ToList(),
            McpBacktestSettingsExposure.Omitted.Select(o => (o.Owner, o.Property, o.Reason)).ToList());

        Assert.Empty(problems);
    }

    [Fact]
    public void BacktestSettings_TheCheckFailsForAPropertyInNeitherListAndForOneInBoth()
    {
        var allowed = McpBacktestSettingsExposure.Allowed.Select(a => (a.Owner, a.Property)).ToList();
        var omitted = McpBacktestSettingsExposure.Omitted.Select(o => (o.Owner, o.Property, o.Reason)).ToList();
        (Type owner, string property) = allowed[0];

        IReadOnlyList<string> missing = ExposureProblems(McpBacktestSettingsExposure.Owners, allowed.Skip(1).ToList(), omitted);
        IReadOnlyList<string> both = ExposureProblems(McpBacktestSettingsExposure.Owners, allowed, omitted.Append((owner, property, "x")).ToList());
        IReadOnlyList<string> noReason = ExposureProblems(McpBacktestSettingsExposure.Owners, allowed, omitted.Select(o => (o.Owner, o.Property, string.Empty)).ToList());
        IReadOnlyList<string> unknown = ExposureProblems(McpBacktestSettingsExposure.Owners, allowed, omitted.Append((owner, "NoSuchProperty", "x")).ToList());

        Assert.Contains(missing, problem => problem.Contains("in neither list", StringComparison.Ordinal) && problem.Contains(property, StringComparison.Ordinal));
        Assert.Contains(both, problem => problem.Contains("in both lists", StringComparison.Ordinal) && problem.Contains(property, StringComparison.Ordinal));
        Assert.Contains(noReason, problem => problem.Contains("no reason", StringComparison.Ordinal));
        Assert.Contains(unknown, problem => problem.Contains("NoSuchProperty", StringComparison.Ordinal));
    }

    [Fact]
    public void BacktestSettings_KeysAreUniqueAndTheBinderAcceptsTheSameKeysAsBefore()
    {
        Assert.Equal(
            new[] { "initialCapital", "commissionFlat", "commissionPerUnit", "slippageRatio", "sizingModel", "sizingParameter", "initialMarginRatio", "maintenanceMarginRatio", "liquidationPenaltyRatio" },
            McpBacktestSettingsExposure.KeysOf(McpBacktestSettingsExposure.AccountSection));
        Assert.Equal(new[] { "stopLossRatio", "takeProfitRatio" }, McpBacktestSettingsExposure.KeysOf(McpBacktestSettingsExposure.RiskSection));
        Assert.Equal(
            new[] { "annualRiskFreeRate", "annualMar", "annualPeriodsD1", "bootstrapSeed", "bootstrapIterations" },
            McpBacktestSettingsExposure.KeysOf(McpBacktestSettingsExposure.ReportSection));
        Assert.Equal("account", BacktestSettingsBinder.AccountArgument);
        Assert.Equal("riskManagement", BacktestSettingsBinder.RiskArgument);
        Assert.Equal("report", BacktestSettingsBinder.ReportArgument);
    }

    [Fact]
    public void BacktestSettings_NoSettingOfTheBacktestWindowIsAGapAnyMore()
    {
        // The support document's coverage table is written from the GAP: prefix; a new gap must be a conscious change of this list.
        // The annual period count and the two bootstrap values were the last gaps (closed by the report keys of sa_run_backtest).
        Assert.Empty(McpBacktestSettingsExposure.Omitted.Where(o => o.Reason.StartsWith(McpBacktestSettingsExposure.GapPrefix, StringComparison.Ordinal)));
    }

    // ---- AT-A.2 the tool description names every key ----------------------------------------------------------------------

    [Fact]
    public void RunBacktestDescription_NamesEveryAcceptedKey()
    {
        McpServerRuntime runtime = CreateRuntime();
        Assert.True(runtime.Tools.TryGet("sa_run_backtest", out RegisteredTool? tool));

        foreach (AllowedBacktestSetting setting in McpBacktestSettingsExposure.Allowed)
        {
            Assert.Contains(setting.Key, tool.Tool.Description, StringComparison.Ordinal);
        }
    }

    // ---- AT-A.3 comparison operators --------------------------------------------------------------------------------------

    [Fact]
    public void BacktestComparisons_EveryOperatorIsOfferedOrExcludedWithAReason()
    {
        foreach (ComparisonOperator comparison in Enum.GetValues<ComparisonOperator>())
        {
            bool offered = BacktestConditionBinder.ComparisonNames.Contains(comparison.ToString(), StringComparer.Ordinal);
            bool excluded = BacktestConditionBinder.ExcludedComparisons.TryGetValue(comparison, out string? reason);
            Assert.True(offered ^ excluded, $"{comparison} must be in exactly one of ComparisonNames and ExcludedComparisons");
            if (excluded)
            {
                Assert.False(string.IsNullOrWhiteSpace(reason), $"{comparison} is excluded without a reason");
            }
        }

        Assert.All(BacktestConditionBinder.ComparisonNames, name => Assert.True(Enum.IsDefined(typeof(ComparisonOperator), Enum.Parse<ComparisonOperator>(name))));
    }

    [Fact]
    public void ScreenerComparisons_EveryOperatorIsOffered()
    {
        Assert.Equal(
            Enum.GetNames<ComparisonOperator>().OrderBy(n => n, StringComparer.Ordinal),
            ScreenerConditionBinder.ComparisonNames.OrderBy(n => n, StringComparer.Ordinal));
    }

    // ---- AT-A.4 indicators --------------------------------------------------------------------------------------------------

    [Fact]
    public void Indicators_EveryRegisteredTypeHasDefaultSettingsAndOnlySettableParameterTypes()
    {
        IIndicatorFactory factory = IndicatorFactory.Default;
        var catalog = new IndicatorCatalog(factory, new ScreenerCatalogProvider());
        IReadOnlyList<IndicatorCatalogEntry> registered = catalog.Entries.Where(entry => entry.IsRegistered).ToList();
        var problems = new List<string>();

        Assert.Equal(Enum.GetValues<IndicatorType>().Distinct().Count(factory.IsRegistered), registered.Count);
        Assert.NotEmpty(registered);
        Assert.Contains(registered, entry => entry.Parameters.Count > 0);
        foreach (IndicatorCatalogEntry entry in registered)
        {
            if (entry.Category is null)
            {
                problems.Add($"{entry.Type}: no default settings (Category is null)");
            }

            if (entry.OutputSeriesNames.Count == 0)
            {
                problems.Add($"{entry.Type}: no output series");
            }

            foreach (IndicatorParameterDescriptor parameter in entry.Parameters)
            {
                bool settable = McpIndicatorParameterExposure.SettableTypes.Contains(parameter.Type, StringComparer.Ordinal);
                bool unsettable = McpIndicatorParameterExposure.UnsettableOnPurpose.ContainsKey(parameter.Type);
                if (settable == unsettable)
                {
                    problems.Add($"{entry.Type}.{parameter.Name}: type '{parameter.Type}' is {(settable ? "both settable and unsettable" : "neither settable nor listed as unsettable on purpose")}");
                }
            }
        }

        Assert.Empty(problems);
    }

    [Fact]
    public void Indicators_TheUnsettableTypesCarryAReasonAndAreNotSettable()
    {
        Assert.All(McpIndicatorParameterExposure.UnsettableOnPurpose, pair => Assert.False(string.IsNullOrWhiteSpace(pair.Value)));
        Assert.Empty(McpIndicatorParameterExposure.UnsettableOnPurpose.Keys.Intersect(McpIndicatorParameterExposure.SettableTypes, StringComparer.Ordinal));
    }

    // ---- helpers ------------------------------------------------------------------------------------------------------------

    /// <summary>The ways a classification can be wrong: a property in neither list or in both, an unknown property, an omission without a reason.</summary>
    private static IReadOnlyList<string> ExposureProblems(
        IEnumerable<Type> owners,
        IReadOnlyList<(Type Owner, string Property)> allowed,
        IReadOnlyList<(Type Owner, string Property, string Reason)> omitted)
    {
        var problems = new List<string>();
        var actual = new HashSet<(Type, string)>();
        foreach (Type owner in owners)
        {
            foreach (PropertyInfo property in owner.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.GetSetMethod() is not null))
            {
                actual.Add((owner, property.Name));
                bool isAllowed = allowed.Any(a => a.Owner == owner && a.Property == property.Name);
                bool isOmitted = omitted.Any(o => o.Owner == owner && o.Property == property.Name);
                if (isAllowed && isOmitted)
                {
                    problems.Add($"{owner.Name}.{property.Name} is in both lists");
                }
                else if (!isAllowed && !isOmitted)
                {
                    problems.Add($"{owner.Name}.{property.Name} is in neither list");
                }
            }
        }

        foreach ((Type owner, string property) in allowed)
        {
            if (!actual.Contains((owner, property)))
            {
                problems.Add($"{owner.Name}.{property} is allowed but is not a settable property");
            }
        }

        foreach ((Type owner, string property, string reason) in omitted)
        {
            if (!actual.Contains((owner, property)))
            {
                problems.Add($"{owner.Name}.{property} is omitted but is not a settable property");
            }

            if (string.IsNullOrWhiteSpace(reason))
            {
                problems.Add($"{owner.Name}.{property} is omitted with no reason");
            }
        }

        return problems;
    }

    private McpServerRuntime CreateRuntime()
    {
        McpServerRuntime runtime = McpServerComposition.Create(McpTestSupport.EmptyDataConfiguration(_emptyRoot), new LogSink(new StringWriter(), quiet: true));
        _runtimes.Add(runtime);
        return runtime;
    }
}
