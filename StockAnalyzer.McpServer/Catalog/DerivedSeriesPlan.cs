using System.Text.Json;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.McpServer.Limits;
using StockAnalyzer.McpServer.Tools;

namespace StockAnalyzer.McpServer.Catalog;

/// <summary>One side of a derived series: a series of the same call (<see cref="ReferenceId"/>, <see cref="SeriesName"/>) or a constant.</summary>
/// <param name="ReferenceId">Id of an indicator of the call or of an earlier derived series; null for a constant.</param>
/// <param name="SeriesName">The listed output series of the referenced indicator (its main one when none was written); null for a derived series (its single series) and for a constant.</param>
/// <param name="Constant">The constant; null for a reference.</param>
internal sealed record DerivedOperand(string? ReferenceId, string? SeriesName, decimal? Constant);

/// <summary>One validated element of <c>derived</c>.</summary>
internal sealed record DerivedSeriesRequest(int Index, string Id, SeriesOperation Operation, DerivedOperand Left, DerivedOperand Right)
{
    public string Path => $"derived[{Index}]";
}

/// <summary>What a derived series reads from an indicator of the same call, after the pipeline ran.</summary>
/// <param name="IsSuccessful">The indicator's own isSuccessful.</param>
/// <param name="Series">The returned output series, aligned to the call's bars, by listed name.</param>
/// <param name="NonCausalSeries">The listed names of the series that look ahead.</param>
internal sealed record DerivedSeriesSource(bool IsSuccessful, IReadOnlyList<(string Name, IReadOnlyList<decimal?> Values)> Series, IReadOnlyList<string> NonCausalSeries);

/// <summary>The result of one derived series; <see cref="Values"/> is null when it did not succeed.</summary>
internal sealed record DerivedSeriesOutcome(DerivedSeriesRequest Request, bool IsSuccessful, string? ErrorMessage, IReadOnlyList<decimal?>? Values, int? DivisionByZeroCount, bool IsNonCausal);

/// <summary>
/// The <c>derived</c> argument of sa_calculate_indicators: parses and validates the binary operations on indicator series before any data is loaded, and evaluates them
/// on the aligned series afterwards. The arithmetic itself is <see cref="SeriesArithmetic"/> in Core; nothing here is a formula.
/// </summary>
internal static class DerivedSeriesPlan
{
    /// <summary>The keys of an operand: a reference to a series of the call (optionally naming the output series) or a constant.</summary>
    internal const string ReferenceKey = "ref";
    internal const string SeriesNameKey = "outputSeriesName";
    internal const string ConstantKey = "constant";

    /// <summary>The keys of one element of <c>derived</c>.</summary>
    internal const string IdKey = "id";
    internal const string OperationKey = "op";
    internal const string LeftKey = "left";
    internal const string RightKey = "right";

    public static IReadOnlyList<DerivedSeriesRequest> Parse(JsonElement derived, IReadOnlyList<RequestedIndicator> indicators, IndicatorChainPlan chain, McpLimits limits)
    {
        if (derived.GetArrayLength() > limits.MaxDerivedSeriesPerCall)
        {
            throw new ToolException(
                ToolErrorCodes.LimitExceeded,
                $"At most {limits.MaxDerivedSeriesPerCall} derived series per call.",
                "derived",
                $"Send {limits.MaxDerivedSeriesPerCall} or fewer, or split the call.");
        }

        var indicatorsById = indicators.ToDictionary(i => i.Id, StringComparer.Ordinal);
        var declared = new List<string>();
        foreach (JsonElement item in derived.EnumerateArray())
        {
            declared.Add(item.GetProperty(IdKey).GetString()!);
        }

        var requests = new List<DerivedSeriesRequest>();
        var earlier = new HashSet<string>(StringComparer.Ordinal);
        int index = 0;
        foreach (JsonElement item in derived.EnumerateArray())
        {
            string path = $"derived[{index}]";
            string id = declared[index];
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new ToolException(ToolErrorCodes.InvalidArgument, "A derived series id must not be empty.", $"{path}.{IdKey}");
            }

            if (indicatorsById.ContainsKey(id) || !earlier.Add(id))
            {
                throw new ToolException(
                    ToolErrorCodes.InvalidArgument,
                    $"Id '{id}' is used more than once.",
                    $"{path}.{IdKey}",
                    "Ids must be unique across indicators and derived series within one call.");
            }

            SeriesOperation operation = ParseOperation(item.GetProperty(OperationKey).GetString()!, $"{path}.{OperationKey}");
            DerivedOperand left = ParseOperand(item.GetProperty(LeftKey), $"{path}.{LeftKey}", id, earlier, declared, indicatorsById, chain);
            DerivedOperand right = ParseOperand(item.GetProperty(RightKey), $"{path}.{RightKey}", id, earlier, declared, indicatorsById, chain);
            requests.Add(new DerivedSeriesRequest(index, id, operation, left, right));
            index++;
        }

        return requests;
    }

    /// <summary>
    /// Evaluates the derived series in declaration order, each on the series of the call's bars (<paramref name="barCount"/> values). A series whose input did not succeed
    /// is reported as failed with that input named; the call as a whole is not failed.
    /// </summary>
    public static IReadOnlyList<DerivedSeriesOutcome> Evaluate(IReadOnlyList<DerivedSeriesRequest> requests, Func<string, DerivedSeriesSource> indicatorSource, int barCount)
    {
        var outcomes = new Dictionary<string, DerivedSeriesOutcome>(StringComparer.Ordinal);
        var ordered = new List<DerivedSeriesOutcome>(requests.Count);
        foreach (DerivedSeriesRequest request in requests)
        {
            DerivedSeriesOutcome outcome = EvaluateOne(request, indicatorSource, outcomes, barCount);
            outcomes[request.Id] = outcome;
            ordered.Add(outcome);
        }

        return ordered;
    }

    private static DerivedSeriesOutcome EvaluateOne(
        DerivedSeriesRequest request, Func<string, DerivedSeriesSource> indicatorSource, Dictionary<string, DerivedSeriesOutcome> derivedById, int barCount)
    {
        bool nonCausal = false;
        string? failure = null;
        IReadOnlyList<decimal?>? left = Resolve(request.Left, LeftKey);
        IReadOnlyList<decimal?>? right = Resolve(request.Right, RightKey);
        if (failure is not null || left is null || right is null)
        {
            return new DerivedSeriesOutcome(request, false, failure ?? "An input could not be read.", null, null, nonCausal);
        }

        DerivedSeries calculated = SeriesArithmetic.Apply(request.Operation, left, right, barCount);
        if (calculated.Values.All(v => v is null))
        {
            string reason = calculated.DivisionByZeroCount > 0
                ? $"No value could be calculated: every value is null ({calculated.DivisionByZeroCount} bar(s) had a zero divisor)."
                : "No value could be calculated: every value is null (an input has no value at the bars where the other has one).";
            return new DerivedSeriesOutcome(request, false, reason, null, calculated.DivisionByZeroCount, nonCausal);
        }

        return new DerivedSeriesOutcome(request, true, null, calculated.Values, calculated.DivisionByZeroCount, nonCausal);

        IReadOnlyList<decimal?>? Resolve(DerivedOperand operand, string side)
        {
            if (operand.Constant is { } constant)
            {
                return Enumerable.Repeat<decimal?>(constant, barCount).ToArray();
            }

            string id = operand.ReferenceId!;
            if (derivedById.TryGetValue(id, out DerivedSeriesOutcome? upstream))
            {
                if (upstream.IsSuccessful)
                {
                    nonCausal |= upstream.IsNonCausal;
                    return upstream.Values;
                }

                failure ??= $"Input '{side}' is the derived series '{id}', which did not succeed: {upstream.ErrorMessage}";
                return null;
            }

            DerivedSeriesSource source = indicatorSource(id);
            if (!source.IsSuccessful)
            {
                failure ??= $"Input '{side}' is the indicator '{id}', which did not succeed; see results.{id}.errorMessage.";
                return null;
            }

            foreach ((string name, IReadOnlyList<decimal?> values) in source.Series)
            {
                if (string.Equals(name, operand.SeriesName, StringComparison.Ordinal))
                {
                    nonCausal |= source.NonCausalSeries.Contains(name, StringComparer.Ordinal);
                    return values;
                }
            }

            failure ??= $"Input '{side}' is the series '{operand.SeriesName}' of the indicator '{id}', which returned no such series.";
            return null;
        }
    }

    private static SeriesOperation ParseOperation(string text, string path)
    {
        string[] names = Enum.GetNames<SeriesOperation>();
        if (!names.Contains(text, StringComparer.Ordinal))
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"Unknown operation '{text}'.", path, $"Operations (exact, case-sensitive): {string.Join(", ", names)}.");
        }

        return Enum.Parse<SeriesOperation>(text);
    }

    private static DerivedOperand ParseOperand(
        JsonElement operand,
        string path,
        string ownId,
        HashSet<string> earlier,
        IReadOnlyList<string> declared,
        Dictionary<string, RequestedIndicator> indicatorsById,
        IndicatorChainPlan chain)
    {
        foreach (JsonProperty property in operand.EnumerateObject())
        {
            if (property.Name is not (ReferenceKey or SeriesNameKey or ConstantKey))
            {
                throw new ToolException(
                    ToolErrorCodes.InvalidArgument,
                    $"Unknown key '{property.Name}' in an operand.",
                    $"{path}.{property.Name}",
                    $"An operand is {{\"{ReferenceKey}\":\"<id>\"}}, {{\"{ReferenceKey}\":\"<id>\",\"{SeriesNameKey}\":\"<name>\"}} or {{\"{ConstantKey}\":<number>}}.");
            }
        }

        bool hasReference = operand.TryGetProperty(ReferenceKey, out JsonElement reference);
        bool hasConstant = operand.TryGetProperty(ConstantKey, out JsonElement constant);
        bool hasSeriesName = operand.TryGetProperty(SeriesNameKey, out JsonElement seriesName);
        if (hasReference == hasConstant)
        {
            throw new ToolException(
                ToolErrorCodes.InvalidArgument,
                $"An operand needs exactly one of '{ReferenceKey}' and '{ConstantKey}'.",
                path,
                $"Use {{\"{ReferenceKey}\":\"<id>\"}} or {{\"{ConstantKey}\":<number>}}.");
        }

        if (hasConstant)
        {
            if (hasSeriesName)
            {
                throw new ToolException(ToolErrorCodes.InvalidArgument, $"'{SeriesNameKey}' belongs to a '{ReferenceKey}' operand.", $"{path}.{SeriesNameKey}");
            }

            if (constant.ValueKind != JsonValueKind.Number || !constant.TryGetDecimal(out decimal value))
            {
                throw new ToolException(ToolErrorCodes.InvalidArgument, $"'{ConstantKey}' must be a number that fits a decimal.", $"{path}.{ConstantKey}");
            }

            return new DerivedOperand(null, null, value);
        }

        if (reference.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(reference.GetString()))
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"'{ReferenceKey}' must be the id of an indicator or of an earlier derived series.", $"{path}.{ReferenceKey}");
        }

        string id = reference.GetString()!;
        string? requestedSeries = null;
        if (hasSeriesName)
        {
            if (seriesName.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(seriesName.GetString()))
            {
                throw new ToolException(ToolErrorCodes.InvalidArgument, $"'{SeriesNameKey}' must be an output series name.", $"{path}.{SeriesNameKey}");
            }

            requestedSeries = seriesName.GetString()!.Trim();
        }

        if (indicatorsById.TryGetValue(id, out RequestedIndicator? indicator))
        {
            return new DerivedOperand(id, ResolveIndicatorSeries(indicator, requestedSeries, $"{path}.{SeriesNameKey}", chain), null);
        }

        if (earlier.Contains(id) && !string.Equals(id, ownId, StringComparison.Ordinal))
        {
            if (requestedSeries is not null && !string.Equals(requestedSeries, IndicatorResult.MainSeriesName, StringComparison.OrdinalIgnoreCase))
            {
                throw new ToolException(
                    ToolErrorCodes.InvalidOutputSeries,
                    $"A derived series has one output series, '{IndicatorResult.MainSeriesName}'.",
                    $"{path}.{SeriesNameKey}",
                    $"Omit {SeriesNameKey} or use '{IndicatorResult.MainSeriesName}'.");
            }

            return new DerivedOperand(id, null, null);
        }

        if (string.Equals(id, ownId, StringComparison.Ordinal))
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"Derived series '{ownId}' cannot use itself as an input (circular reference).", path, "Reference an indicator or a derived series declared before it.");
        }

        if (declared.Contains(id, StringComparer.Ordinal))
        {
            throw new ToolException(
                ToolErrorCodes.InvalidArgument,
                $"'{id}' is a derived series declared after '{ownId}'; a derived series can only read the ones declared before it.",
                path,
                "Move the series it reads earlier in the derived array.");
        }

        throw new ToolException(
            ToolErrorCodes.InvalidArgument,
            $"'{id}' is neither an indicator id nor a derived series id of this call.",
            path,
            "Reference the id of an entry of the indicators array or of an earlier derived series.");
    }

    private static string ResolveIndicatorSeries(RequestedIndicator indicator, string? requested, string path, IndicatorChainPlan chain)
    {
        if (requested is null)
        {
            return chain.MainSeriesDisplayName(indicator)
                ?? throw new ToolException(
                    ToolErrorCodes.InvalidOutputSeries,
                    $"{indicator.Entry.Type} has no listed output series that is its main series.",
                    path,
                    $"Name one of: {string.Join(", ", indicator.Entry.OutputSeriesNames)}.");
        }

        return indicator.Entry.OutputSeriesNames.FirstOrDefault(n => string.Equals(n, requested, StringComparison.OrdinalIgnoreCase))
            ?? throw new ToolException(
                ToolErrorCodes.InvalidOutputSeries,
                $"{indicator.Entry.Type} has no output series '{requested}'.",
                path,
                $"Output series of {indicator.Entry.Type}: {string.Join(", ", indicator.Entry.OutputSeriesNames)}.");
    }
}
