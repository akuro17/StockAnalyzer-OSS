using System.Globalization;
using System.Text;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.McpServer.Limits;
using StockAnalyzer.McpServer.Tools;

namespace StockAnalyzer.McpServer.Prompts;

/// <summary>One argument of a prompt: its name, what it is for, whether it must be given and the value used when it is not (null when there is none).</summary>
internal sealed record McpPromptArgument(string Name, string Description, bool Required, string? DefaultValue = null);

/// <summary>The checked arguments of one prompts/get call: every declared argument has its value (given or default), except an optional argument without a default, which is absent.</summary>
internal sealed record McpPromptValues(IReadOnlyDictionary<string, string> Values)
{
    public string this[string name] => Values[name];

    public string? Optional(string name) => Values.TryGetValue(name, out string? value) ? value : null;
}

/// <summary>
/// A workflow prompt: a fixed, deterministic list of read-only tool calls an AI client is asked to make for one analysis, and how to report it. The text depends only on the argument values:
/// no clock, no stored data and no setting is read, so the same arguments always give the same prompt.
/// </summary>
internal sealed record McpPrompt(string Name, string Title, string Description, IReadOnlyList<McpPromptArgument> Arguments, Func<McpPromptValues, string> BuildText);

/// <summary>The argument was missing, unknown, not a string or out of its range; the message is the -32602 text.</summary>
internal sealed class McpPromptArgumentException : Exception
{
    public McpPromptArgumentException(string message) : base(message)
    {
    }
}

/// <summary>
/// The prompts of the server (MCP <c>prompts/list</c> and <c>prompts/get</c>, FU6 of the WebAI expansion plan): three quantitative workflows that name only tools this server serves
/// and that never write anything. A prompt is advice to the client about which tools to call in which order; the tools keep every check of their own. Every tool name in a prompt text is checked against
/// the tool list by a test, so a renamed tool cannot leave a stale prompt behind.
/// </summary>
internal sealed class McpPromptRegistry
{
    public const string TechnicalDiagnosisName = "technical_diagnosis";
    public const string SectorRotationName = "sector_rotation_analysis";
    public const string BacktestAuditName = "backtest_overfitting_audit";

    public const string SymbolArgument = "symbol";
    public const string TimeFrameArgument = "timeFrame";
    public const string GroupIdArgument = "groupId";
    public const string LookbackBarsArgument = "lookbackBars";
    public const string TemplateIdArgument = "templateId";

    /// <summary>The time frame used when technical_diagnosis is not given one.</summary>
    public const string DefaultTimeFrame = nameof(TimeFrame.D1);

    /// <summary>The bars sector_rotation_analysis looks back when it is not given a number (daily bars).</summary>
    public const int DefaultSectorLookbackBars = 200;

    /// <summary>The last line of every prompt: the report is written in the language the user asked in (the prompt text itself is English, like every text of the server).</summary>
    private const string ReportLanguageLine = "Write the report in the language of the user's request.";

    private const string ClosingLine = "This is an analysis of stored data, not a recommendation to trade.";

    private readonly Dictionary<string, McpPrompt> _byName;
    private readonly int _maxLookbackBars;
    private readonly int _defaultMinInSampleTrades;

    public McpPromptRegistry(McpLimits limits)
    {
        _maxLookbackBars = limits.MaxCandlesPerRequest;
        _defaultMinInSampleTrades = limits.DefaultMinInSampleTrades;
        Prompts = new[]
        {
            new McpPrompt(
                TechnicalDiagnosisName,
                "Technical diagnosis of one symbol",
                "Trend, momentum, support and resistance, market structure, patterns and divergences of one stored symbol from read-only tools, reported with dates and numbers.",
                new[]
                {
                    new McpPromptArgument(SymbolArgument, "Ticker symbol, for example 7203-T (the form every tool accepts; a formula of two symbols is allowed).", Required: true),
                    new McpPromptArgument(TimeFrameArgument, $"Time frame: {SupportedTimeFrameList()}.", Required: false, DefaultTimeFrame),
                },
                TechnicalDiagnosis),
            new McpPrompt(
                SectorRotationName,
                "Sector rotation analysis",
                "Breadth and relative strength of a ticker group over a look-back window, to say which parts of the market lead and which lag.",
                new[]
                {
                    new McpPromptArgument(GroupIdArgument, "Id (GUID) of a ticker group from sa_list_ticker_groups. When omitted the workflow starts by listing the groups.", Required: false),
                    new McpPromptArgument(LookbackBarsArgument, $"Daily bars to look back, 1 to {_maxLookbackBars.ToString(CultureInfo.InvariantCulture)}.", Required: false, DefaultSectorLookbackBars.ToString(CultureInfo.InvariantCulture)),
                },
                SectorRotation),
            new McpPrompt(
                BacktestAuditName,
                "Backtest overfitting audit",
                "Checks whether a saved backtest template on one symbol holds up outside the data it was tuned on: baseline, walk-forward windows and parameter sensitivity.",
                new[]
                {
                    new McpPromptArgument(SymbolArgument, "Ticker symbol the template is tested on.", Required: true),
                    new McpPromptArgument(TemplateIdArgument, "Id (GUID) of a saved backtest-condition template from sa_list_templates.", Required: true),
                },
                BacktestAudit),
        };
        _byName = Prompts.ToDictionary(prompt => prompt.Name, StringComparer.Ordinal);
    }

    public IReadOnlyList<McpPrompt> Prompts { get; }

    public bool TryGet(string name, out McpPrompt? prompt) => _byName.TryGetValue(name, out prompt);

    /// <summary>
    /// Checks the arguments of a prompts/get call and gives the prompt text. Unknown, non-string and out-of-range arguments, and a missing required argument, are <see cref="McpPromptArgumentException"/>
    /// (the dispatcher answers -32602).
    /// </summary>
    public string Build(McpPrompt prompt, IReadOnlyDictionary<string, string> given)
    {
        foreach (string name in given.Keys)
        {
            if (prompt.Arguments.All(argument => argument.Name != name))
            {
                throw new McpPromptArgumentException($"Unknown argument '{name}' for prompt '{prompt.Name}'. Arguments: {string.Join(", ", prompt.Arguments.Select(argument => argument.Name))}.");
            }
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (McpPromptArgument argument in prompt.Arguments)
        {
            if (given.TryGetValue(argument.Name, out string? value))
            {
                values[argument.Name] = Check(argument.Name, value);
            }
            else if (argument.Required)
            {
                throw new McpPromptArgumentException($"Missing required argument '{argument.Name}' for prompt '{prompt.Name}'.");
            }
            else if (argument.DefaultValue is not null)
            {
                values[argument.Name] = argument.DefaultValue;
            }
        }

        return prompt.BuildText(new McpPromptValues(values));
    }

    private string Check(string name, string value)
    {
        switch (name)
        {
            case SymbolArgument:
                // The one rule of every tool (TickerSymbol.IsWellFormed through SymbolResolver.Canonical): at most 32 characters, no whitespace, path or drive separators or control characters;
                // a formula of two symbols is allowed. The text gets the canonical form, as the tools use it. Whether the symbol is stored is the tools' answer, not the prompt's.
                try
                {
                    return SymbolResolver.Canonical(value, SymbolArgument);
                }
                catch (ToolException error)
                {
                    throw new McpPromptArgumentException($"'{SymbolArgument}': {error.Message}{(error.Hint is null ? string.Empty : " " + error.Hint)}");
                }

            case TimeFrameArgument:
                if (!ReadOnlyGatewayContract.SupportedTimeFrames.Any(frame => string.Equals(frame.ToString(), value, StringComparison.Ordinal)))
                {
                    throw new McpPromptArgumentException($"'{TimeFrameArgument}' must be one of: {SupportedTimeFrameList()}.");
                }

                return value;
            case LookbackBarsArgument:
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int bars) || bars < 1 || bars > _maxLookbackBars)
                {
                    throw new McpPromptArgumentException($"'{LookbackBarsArgument}' must be a whole number from 1 to {_maxLookbackBars.ToString(CultureInfo.InvariantCulture)}.");
                }

                return bars.ToString(CultureInfo.InvariantCulture);
            case GroupIdArgument:
            case TemplateIdArgument:
                // Read as the tools read an id (Guid.TryParse of the trimmed text); the text gets the lower-case "D" form that sa_list_ticker_groups and sa_list_templates print.
                if (!Guid.TryParse(value.Trim(), out Guid id))
                {
                    throw new McpPromptArgumentException($"'{name}' must be a GUID like 00000000-0000-0000-0000-000000000000.");
                }

                return id.ToString("D");
            default:
                throw new McpPromptArgumentException($"Unknown argument '{name}'.");
        }
    }

    private static string SupportedTimeFrameList() => string.Join(", ", ReadOnlyGatewayContract.SupportedTimeFrames);

    private static string TechnicalDiagnosis(McpPromptValues values)
    {
        string symbol = values[SymbolArgument];
        string frame = values[TimeFrameArgument];
        var text = new StringBuilder();
        text.AppendLine($"Diagnose {symbol} on the {frame} time frame from the stored bars of the StockAnalyzer MCP server. Use only the tools below, in this order, and take every number from a tool result (never from a picture).");
        text.AppendLine();
        text.AppendLine($"1. sa_get_candles with symbol {symbol} and timeFrame {frame}: the latest bars. Note the last bar date; every later step uses it as the end of the data and nothing after it may be assumed. Pass compactRows true and decimalPlaces 4 to reduce token usage.");
        text.AppendLine("2. sa_calculate_indicators on the same symbol and time frame: a trend average pair (for example SMA or EMA), a momentum oscillator (for example RSI), MACD and a volatility measure (for example ATR). Type names and parameters come from sa_get_capabilities. Pass includeSeries false, summary true and decimalPlaces 4 to receive compact statistics instead of full bar arrays.");
        text.AppendLine("3. sa_analyze_market_structure: the swing highs and lows with the date each was confirmed, and the current structure (higher highs and lows, or the reverse).");
        text.AppendLine("4. sa_find_support_resistance: the nearest levels above and below the last close.");
        text.AppendLine("5. sa_detect_candle_patterns and sa_detect_chart_patterns: patterns that ended on or near the last bar.");
        text.AppendLine("6. sa_detect_divergences: divergences between price and the oscillators of step 2.");
        text.AppendLine("7. Optional: sa_render_chart_image with the same symbol and time frame, only as a reading aid; the numbers still come from the tools above. Pass includeBarCoordinates false when calling sa_render_chart_image.");
        text.AppendLine();
        text.AppendLine("Report, in this order: trend (direction and strength, with the average values and the date), momentum, key levels (price and distance from the last close in percent), patterns and divergences (with their dates), and the risks that would invalidate the reading (a level that, if broken, changes the picture).");
        text.AppendLine("State the unit of every number (price, percent, oscillator value) and the date it refers to. If a tool returns an error or no data, say so and continue with the remaining steps instead of filling the gap.");
        text.AppendLine(ClosingLine);
        text.AppendLine(ReportLanguageLine);
        return text.ToString();
    }

    private static string SectorRotation(McpPromptValues values)
    {
        string bars = values[LookbackBarsArgument];
        string? groupId = values.Optional(GroupIdArgument);
        var text = new StringBuilder();
        text.AppendLine($"Analyse sector rotation over the last {bars} daily bars from the stored data of the StockAnalyzer MCP server. Use only the tools below, in this order; take every number from a tool result.");
        text.AppendLine();
        if (groupId is null)
        {
            text.AppendLine("1. sa_list_ticker_groups: list the groups and choose the group (or the sibling groups of one parent) that represents the sectors to compare. Name the groups you chose and why.");
        }
        else
        {
            text.AppendLine($"1. sa_list_ticker_groups: confirm that the group {groupId} exists and read what the tool lists for it. If it does not exist, stop and say so.");
        }

        string group = groupId ?? "each chosen group";
        text.AppendLine($"2. sa_calculate_market_breadth with groupId {group}, timeFrame {DefaultTimeFrame} and lookbackBars {bars}: advancers and decliners, the share above its moving average, new highs and lows, and the McClellan values. Compare the groups on the same dates. Pass outputTailCount 20 to return only the recent breadth tail.");
        text.AppendLine($"3. sa_calculate_relative_performance with a benchmark symbol that you choose as symbol (for example the index ETF of the market) and the strongest and weakest members of {group} as comparisonSymbols, timeFrame {DefaultTimeFrame} and lookbackBars {bars}. Choose the mode (Performance, Ratio, ZScore or Spread; the tool default is Performance) that answers the question best. Name the benchmark and the mode you chose and why. Pass outputTailCount 20 and decimalPlaces 4.");
        text.AppendLine();
        text.AppendLine("Report: for each group, the breadth trend (improving, stable or deteriorating, with the start and end values and dates), the leaders and laggards by relative strength, and the rotation you see (which groups gain strength while others lose it). Mark a conclusion as weak when the groups hold few symbols or the windows contain missing data.");
        text.AppendLine("State the unit of every number and the first and last date of the window. If a tool returns an error, say so and continue.");
        text.AppendLine(ClosingLine);
        text.AppendLine(ReportLanguageLine);
        return text.ToString();
    }

    private string BacktestAudit(McpPromptValues values)
    {
        string symbol = values[SymbolArgument];
        string templateId = values[TemplateIdArgument];
        var text = new StringBuilder();
        text.AppendLine($"Audit the backtest template {templateId} on {symbol} for overfitting, using the StockAnalyzer MCP server. Use only the tools below, in this order; take every number from a tool result.");
        text.AppendLine();
        text.AppendLine($"1. sa_get_template_detail with templateType backtestCondition and templateId {templateId}: the entry and exit conditions, their numeric parameters and the risk management. Count the free numeric parameters.");
        text.AppendLine($"2. sa_run_backtest with symbol {symbol}, timeFrame {DefaultTimeFrame} and templateId {templateId} over the whole stored history: the baseline (number of trades, win rate, profit factor, maximum drawdown, total return). Pass compactReport true and maxReturnedTrades 0.");
        text.AppendLine($"3. sa_run_walk_forward_analysis with symbol {symbol}, the conditions of step 1 with {{\"$param\": name}} placeholders where the numbers go, parameterRanges (for each placeholder a name, start, end and step; the range must contain the template's own value with room on both sides), an objective chosen from the list that sa_get_capabilities gives, and windows (a generator with mode Rolling or Anchored, inSampleBars, outOfSampleBars and stepBars, or a list of dated windows). Compare the in-sample and out-of-sample results window by window.");
        text.AppendLine("4. sa_optimize_backtest with the same conditions, parameterRanges and objective, and an outOfSampleStartDate that leaves a meaningful part of the history out of sample: how much the best parameters differ from the template and how fast the result falls when a parameter moves a little.");
        text.AppendLine();
        text.AppendLine($"Report the number of trades of every window and of the baseline. minInSampleTrades (default {_defaultMinInSampleTrades.ToString(CultureInfo.InvariantCulture)}) is only the least a combination must trade to be ranked, not a sign that a window has enough trades: when a window has few trades, say that it proves little.");
        text.AppendLine("Report the verdict as one of: robust, doubtful or overfitted, with the evidence for it: the trades per window, the drop from in-sample to out-of-sample (return, win rate, drawdown), the share of out-of-sample windows that are profitable, and whether neighbouring parameter values give similar results or a sharp peak. State the parameterRanges, the objective and the window sizes you chose, and say which numbers you could not obtain and why.");
        text.AppendLine("State the unit of every number and the date range it covers.");
        text.AppendLine(ClosingLine);
        text.AppendLine(ReportLanguageLine);
        return text.ToString();
    }
}
