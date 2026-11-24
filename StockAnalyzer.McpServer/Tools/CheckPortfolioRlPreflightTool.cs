using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Execution;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using StockAnalyzer.Core.Utils;

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
    private readonly IReadOnlyStockAnalyzerGateway? _gateway;
    private readonly ErrorMessageSanitizer _sanitizer;

    public CheckPortfolioRlPreflightTool(
        SymbolResolver symbols,
        IRlPortfolioService? portfolioService = null,
        IReadOnlyStockAnalyzerGateway? gateway = null,
        ErrorMessageSanitizer? sanitizer = null)
    {
        _symbols = symbols;
        _portfolioService = portfolioService ?? new RlPortfolioService(new RlPortfolioSettings());
        _gateway = gateway;
        _sanitizer = sanitizer ?? new ErrorMessageSanitizer(Array.Empty<string?>());
    }

    public string Name => "sa_check_portfolio_rl_preflight";

    public string Description =>
        "Performs preflight checks for portfolio reinforcement learning across 2 to 100 tickers without modifying Data or executing training. " +
        "Checks date alignment, union calendar length, holdout splits, and per-symbol readiness.";

    public bool IsHeavy => false;

    public bool IsExecution => false;

    public IReadOnlyDictionary<string, string>? MisnamedArgumentHints => DateArgumentHints.ForStartAndEndDateExclusive;

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
            string resolved = await _symbols.ResolveAsync(raw, SymbolsArgument, cancellationToken, allowSynthetic: false).ConfigureAwait(false);
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
        if (modeText is not null && !StrictEnumValidator.TryParseStrict(modeText, out mode))
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
            PanelPreflight probe = await _portfolioService.PreflightAsync(
                resolvedSymbols,
                endExclusive.AddDays(-1),
                endExclusive,
                holdout,
                mode,
                cancellationToken).ConfigureAwait(false);
            var probeRows = new List<(string Symbol, string Status, string Role, int BarsInPeriod, string Detail)>(probe.Symbols.Length);
            foreach (RlPanelSymbolPreflight sym in probe.Symbols)
            {
                string statusText = sym.Status.ToString();
                string detail = sym.Detail ?? string.Empty;
                if (_gateway is not null
                    && sym.Status != RlPanelSymbolStatus.MissingFile
                    && await TryFormatInvalidBarDetailAsync(sym.Symbol, startDateInclusive: null, endExclusive, cancellationToken).ConfigureAwait(false) is { } invalidDetail)
                {
                    statusText = RlPanelSymbolStatus.ReadError.ToString();
                    detail = invalidDetail;
                }

                probeRows.Add((sym.Symbol, statusText, sym.Role.ToString(), 0, _sanitizer.Sanitize(detail)));
            }

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
                foreach ((string symName, string symStatus, string symRole, int barsInPeriod, string detail) in probeRows)
                {
                    writer.WriteStartObject();
                    writer.WriteString("symbol", symName);
                    writer.WriteString("status", symStatus);
                    writer.WriteString("role", symRole);
                    writer.WriteNumber("barsInPeriod", barsInPeriod);
                    writer.WriteString("detail", detail);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            });
        }

        PanelPreflight preflight = await _portfolioService.PreflightAsync(resolvedSymbols, start.Value, endExclusive, holdout, mode, cancellationToken).ConfigureAwait(false);
        // The configured "RlPortfolio" settings (symbol maximum, holdout share) gate the start exactly as in the GUI; only the weight cap is this call's own.
        RlPortfolioSettings settings = _portfolioService.Settings.WithRunOverrides(null, maxWeightPercent);
        var decision = RlPanelStartGate.Decide(preflight, excludeUnavailable: true, holdout, settings);

        var rows = new List<(string Symbol, string Status, string Role, int BarsInPeriod, string Detail)>(preflight.Symbols.Length);
        foreach (RlPanelSymbolPreflight sym in preflight.Symbols)
        {
            string detail = sym.Detail ?? string.Empty;
            if (sym.Status == RlPanelSymbolStatus.ReadError
                && _gateway is not null
                && IsInvalidOhlcvDetail(detail)
                && await TryFormatInvalidBarDetailAsync(sym.Symbol, start.Value, endExclusive, cancellationToken).ConfigureAwait(false) is { } invalidDetail)
            {
                detail = invalidDetail;
            }

            rows.Add((sym.Symbol, sym.Status.ToString(), sym.Role.ToString(), sym.BarsInPeriod, _sanitizer.Sanitize(detail)));
        }

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
            foreach ((string symName, string symStatus, string symRole, int barsInPeriod, string detail) in rows)
            {
                writer.WriteStartObject();
                writer.WriteString("symbol", symName);
                writer.WriteString("status", symStatus);
                writer.WriteString("role", symRole);
                writer.WriteNumber("barsInPeriod", barsInPeriod);
                writer.WriteString("detail", detail);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        });
    }

    private static bool IsInvalidOhlcvDetail(string detail)
        => detail.Contains("OHLCV", StringComparison.OrdinalIgnoreCase)
           || detail.Contains("source bar", StringComparison.OrdinalIgnoreCase);

    private async Task<string?> TryFormatInvalidBarDetailAsync(
        string symbol,
        DateOnly? startDateInclusive,
        DateOnly endExclusive,
        CancellationToken cancellationToken)
    {
        if (_gateway is null)
        {
            return null;
        }

        try
        {
            IReadOnlyList<CandleData> stored = await _gateway.GetCandlesAsync(symbol, TimeFrame.D1, int.MaxValue, cancellationToken).ConfigureAwait(false);
            if (CandleInvariant.TryFindFirstViolation(stored, out CandleData badBar, out IReadOnlyList<CandleViolationCode> codes, startDateInclusive, endExclusive))
            {
                return $"Invalid local source OHLCV; no repair is permitted. First invalid bar: {CandleInvariant.FormatBarViolation(symbol, badBar, codes)}.";
            }

            if (startDateInclusive is not null
                && CandleInvariant.TryFindFirstViolation(stored, out badBar, out codes))
            {
                return $"Invalid local source OHLCV; no repair is permitted. First invalid bar: {CandleInvariant.FormatBarViolation(symbol, badBar, codes)}.";
            }
        }
        catch (DataUnavailableException)
        {
            // Keep the evaluator's own ReadError detail if the stored file cannot be read through the gateway.
        }

        return null;
    }
}
