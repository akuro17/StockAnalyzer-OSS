using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Execution;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Services.ReinforcementLearning;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>sa_check_portfolio_rl_preflight: read-only feasibility and readiness check across 2 to 100 tickers.</summary>
internal sealed class CheckPortfolioRlPreflightTool : IMcpTool
{
    public const string SymbolsArgument = "symbols";
    public const string StartDateArgument = "startDate";
    public const string EndDateExclusiveArgument = "endDateExclusive";
    public const string SymbolHoldoutArgument = "symbolHoldout";
    public const string ModeArgument = "mode";
    public const string MaxAssetWeightPercentArgument = "maxAssetWeightPercent";

    private readonly SymbolResolver _symbols;
    private readonly IRlPortfolioService _portfolioService;

    public CheckPortfolioRlPreflightTool(SymbolResolver symbols, IRlPortfolioService? portfolioService = null)
    {
        _symbols = symbols;
        _portfolioService = portfolioService ?? new RlPortfolioService(new RlPortfolioSettings());
    }

    public string Name => "sa_check_portfolio_rl_preflight";

    public string Description =>
        "Performs preflight checks for portfolio reinforcement learning across 2 to 100 tickers without modifying Data or executing training. " +
        "Checks date alignment, union calendar length, holdout splits, and per-symbol readiness.";

    public bool IsHeavy => false;

    public bool IsExecution => false;

    public string? ExampleArgumentsJson => "{\"symbols\":[\"7203-T\",\"6758-T\",\"9984-T\"],\"mode\":\"Standard\"}";

    public string InputSchemaJson => ToolSchema.Object()
        .StringArray(SymbolsArgument, "The list of tickers in the portfolio universe (2 to 100 symbols).", required: true, minItems: 2)
        .String(StartDateArgument, $"First date of the period ({ToolJson.DateFormat}). If omitted, proposed automatically.")
        .String(EndDateExclusiveArgument, $"First day NOT in the period ({ToolJson.DateFormat}). If omitted, proposed automatically.")
        .Boolean(SymbolHoldoutArgument, "Whether symbol holdout is requested (default: true).")
        .String(ModeArgument, "Evaluation mode (Standard, ShortHistory). Default: Standard.", allowedValues: ["Standard", "ShortHistory"])
        .Integer(MaxAssetWeightPercentArgument, "Maximum asset weight percent between 1 and 100 (default: 100).", defaultValue: 100, minimum: 1, maximum: 100)
        .Build();

    public async Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        IReadOnlyList<string>? rawSymbols = ToolArgs.StringArray(arguments, SymbolsArgument);
        if (rawSymbols is null || rawSymbols.Count < RlPortfolioContract.MinimumSymbols)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"At least {RlPortfolioContract.MinimumSymbols} symbols are required.", SymbolsArgument);
        }

        if (rawSymbols.Count > ExecutionLimits.MaxPortfolioSymbols)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"At most {ExecutionLimits.MaxPortfolioSymbols} symbols are supported.", SymbolsArgument);
        }

        var resolvedSymbols = new List<string>(rawSymbols.Count);
        foreach (string raw in rawSymbols)
        {
            string resolved = await _symbols.ResolveAsync(raw, SymbolsArgument, cancellationToken).ConfigureAwait(false);
            resolvedSymbols.Add(resolved);
        }

        if (resolvedSymbols.Distinct(StringComparer.OrdinalIgnoreCase).Count() != resolvedSymbols.Count)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, "A symbol is listed twice.", SymbolsArgument);
        }

        DateOnly? start = ToolArgs.Date(arguments, StartDateArgument);
        DateOnly? end = ToolArgs.Date(arguments, EndDateExclusiveArgument);
        if (start is not null && end is not null && start >= end)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"'{StartDateArgument}' must be before '{EndDateExclusiveArgument}'.", StartDateArgument);
        }

        bool holdout = ToolArgs.Boolean(arguments, SymbolHoldoutArgument) ?? true;
        int maxWeightPercent = ToolArgs.PositiveWithin(arguments, MaxAssetWeightPercentArgument, 100, 100);

        string? modeText = ToolArgs.String(arguments, ModeArgument);
        RlPanelEvaluationMode mode = RlPanelEvaluationMode.Standard;
        if (modeText is not null && !Enum.TryParse(modeText, ignoreCase: true, out mode))
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"Unknown mode '{modeText}'.", ModeArgument);
        }

        DateOnly endExclusive = end ?? DateOnly.FromDateTime(DateTime.UtcNow);
        if (start is null)
        {
            RlStartSuggestion? suggestion = await _portfolioService.SuggestStartAsync(resolvedSymbols, endExclusive, mode, cancellationToken).ConfigureAwait(false);
            start = suggestion?.Start;
        }

        if (start is null)
        {
            return ToolJson.Build(writer =>
            {
                writer.WriteStartObject();
                writer.WriteBoolean("canStart", false);
                writer.WriteString("blocker", "NoUsableData");
                writer.WriteBoolean("feasible", false);
                writer.WriteBoolean("symbolHoldout", holdout);
                writer.WriteNumber("calendarLength", 0);
                writer.WriteNumber("readyCount", 0);
                writer.WriteStartArray("trainingSymbols");
                writer.WriteEndArray();
                writer.WriteStartArray("excludedSymbols");
                foreach (string s in resolvedSymbols)
                {
                    writer.WriteStringValue(s);
                }
                writer.WriteEndArray();
                writer.WriteStartArray("symbolDetails");
                writer.WriteEndArray();
                writer.WriteEndObject();
            });
        }

        PanelPreflight preflight = await _portfolioService.PreflightAsync(resolvedSymbols, start.Value, endExclusive, holdout, mode, cancellationToken).ConfigureAwait(false);
        // The configured "RlPortfolio" settings (symbol maximum, holdout share) gate the start exactly as in the GUI; only the weight cap is this call's own.
        RlPortfolioSettings settings = _portfolioService.Settings.WithRunOverrides(null, maxWeightPercent);
        var decision = RlPanelStartGate.Decide(preflight, excludeUnavailable: true, holdout, settings);

        return ToolJson.Build(writer =>
        {
            writer.WriteStartObject();
            writer.WriteBoolean("canStart", decision.CanStart);
            writer.WriteString("blocker", decision.Blocker.ToString());
            writer.WriteBoolean("feasible", preflight.Feasible);
            writer.WriteBoolean("symbolHoldout", decision.SymbolHoldout);
            writer.WriteNumber("calendarLength", preflight.CalendarLength);
            writer.WriteNumber("readyCount", preflight.ReadyCount);
            writer.WriteString("startDate", start.Value.ToString(ToolJson.DateFormat, System.Globalization.CultureInfo.InvariantCulture));
            writer.WriteString("endDateExclusive", endExclusive.ToString(ToolJson.DateFormat, System.Globalization.CultureInfo.InvariantCulture));
            writer.WriteStartArray("trainingSymbols");
            foreach (string s in decision.TrainingSymbols)
            {
                writer.WriteStringValue(s);
            }
            writer.WriteEndArray();
            writer.WriteStartArray("excludedSymbols");
            foreach (string s in decision.ExcludedSymbols)
            {
                writer.WriteStringValue(s);
            }
            writer.WriteEndArray();
            writer.WriteStartArray("symbolDetails");
            foreach (RlPanelSymbolPreflight sym in preflight.Symbols)
            {
                writer.WriteStartObject();
                writer.WriteString("symbol", sym.Symbol);
                writer.WriteString("status", sym.Status.ToString());
                writer.WriteString("role", sym.Role.ToString());
                writer.WriteNumber("barsInPeriod", sym.BarsInPeriod);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        });
    }
}
