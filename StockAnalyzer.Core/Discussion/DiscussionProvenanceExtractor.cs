using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace StockAnalyzer.Core.Discussion;

/// <summary>
/// Recommended canonical analysis hypotheses and intents.
/// </summary>
public static class DiscussionAnalysisIntent
{
    public const string TrendDetection = "TrendDetection";
    public const string ReversalDetection = "ReversalDetection";
    public const string OverboughtOversold = "OverboughtOversold";
    public const string MomentumAnalysis = "MomentumAnalysis";
    public const string DivergenceDetection = "DivergenceDetection";
    public const string VolatilityAnalysis = "VolatilityAnalysis";
    public const string SupportResistance = "SupportResistance";
    public const string VolumeStructure = "VolumeStructure";
    public const string RelativeStrength = "RelativeStrength";
    public const string RegimeClassification = "RegimeClassification";
    public const string RiskAssessment = "RiskAssessment";
}

/// <summary>
/// Validates and normalizes <c>analysisIntent</c> and <see cref="DiscussionInterpretation"/>.
/// </summary>
public static class DiscussionEvidenceValidator
{
    private static readonly JsonWriterOptions CanonicalWriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// Normalizes and validates an analysis intent string.
    /// Returns true if null or valid; false if invalid length or contains control characters.
    /// </summary>
    public static bool TryNormalizeAnalysisIntent(string? rawIntent, out string? normalized)
    {
        if (rawIntent is null)
        {
            normalized = null;
            return true;
        }

        string formC = rawIntent.Normalize(NormalizationForm.FormC).Trim(' ', '\t');
        if (formC.Length == 0 || formC.Length > DiscussionEvidenceLimits.MaxAnalysisIntentChars)
        {
            normalized = null;
            return false;
        }

        foreach (char c in formC)
        {
            if (char.IsControl(c))
            {
                normalized = null;
                return false;
            }
        }

        normalized = formC;
        return true;
    }

    /// <summary>
    /// Validates an interpretation object with flexible partial-input support.
    /// At least one of the 6 subfields must be populated. Computes <see cref="DiscussionInterpretation.ProvidedFields"/>.
    /// </summary>
    public static bool TryValidateInterpretation(DiscussionInterpretation? input, out DiscussionInterpretation? normalized, out string? errorReason)
    {
        if (input is null)
        {
            normalized = null;
            errorReason = null;
            return true;
        }

        string? normSummary = null;
        if (input.Summary is not null)
        {
            normSummary = input.Summary.Normalize(NormalizationForm.FormC).Trim();
            if (normSummary.Length == 0
                || normSummary.Length > DiscussionEvidenceLimits.MaxInterpretationSummaryChars
                || !DiscussionText.HasVisibleCharacter(normSummary)
                || DiscussionText.IsInvalid(normSummary, allowTabLineBreaks: true))
            {
                normalized = null;
                errorReason = $"summary must contain a visible character, contain no disallowed control characters, and be at most {DiscussionEvidenceLimits.MaxInterpretationSummaryChars} characters.";
                return false;
            }
        }

        string? normDirection = null;
        if (input.Direction is not null)
        {
            string dir = input.Direction.Trim();
            if (dir is not (DiscussionInterpretationDirection.Bullish or DiscussionInterpretationDirection.Bearish or DiscussionInterpretationDirection.Neutral))
            {
                normalized = null;
                errorReason = "direction must be 'Bullish', 'Bearish', or 'Neutral'.";
                return false;
            }

            normDirection = dir;
        }

        List<string>? normFindings = null;
        if (input.Findings is not null)
        {
            if (input.Findings.Count > DiscussionEvidenceLimits.MaxInterpretationListItems)
            {
                normalized = null;
                errorReason = $"findings cannot have more than {DiscussionEvidenceLimits.MaxInterpretationListItems} items.";
                return false;
            }

            normFindings = new List<string>(input.Findings.Count);
            foreach (string item in input.Findings)
            {
                if (string.IsNullOrWhiteSpace(item))
                {
                    normalized = null;
                    errorReason = "findings items must not be blank.";
                    return false;
                }

                string cleaned = item.Normalize(NormalizationForm.FormC).Trim(' ', '\t');
                if (cleaned.Length > DiscussionEvidenceLimits.MaxInterpretationItemChars
                    || DiscussionText.IsInvalid(cleaned, allowTabLineBreaks: false))
                {
                    normalized = null;
                    errorReason = "findings item is too long or contains control characters.";
                    return false;
                }

                normFindings.Add(cleaned);
            }
        }

        List<string>? normSupporting = null;
        if (input.SupportingEvidence is not null)
        {
            if (input.SupportingEvidence.Count > DiscussionEvidenceLimits.MaxInterpretationListItems)
            {
                normalized = null;
                errorReason = $"supportingEvidence cannot have more than {DiscussionEvidenceLimits.MaxInterpretationListItems} items.";
                return false;
            }

            normSupporting = new List<string>(input.SupportingEvidence.Count);
            foreach (string item in input.SupportingEvidence)
            {
                if (string.IsNullOrWhiteSpace(item))
                {
                    normalized = null;
                    errorReason = "supportingEvidence items must not be blank.";
                    return false;
                }

                string cleaned = item.Normalize(NormalizationForm.FormC).Trim(' ', '\t');
                if (cleaned.Length > DiscussionEvidenceLimits.MaxInterpretationItemChars
                    || DiscussionText.IsInvalid(cleaned, allowTabLineBreaks: false))
                {
                    normalized = null;
                    errorReason = "supportingEvidence item is too long or contains control characters.";
                    return false;
                }

                normSupporting.Add(cleaned);
            }
        }

        List<string>? normCounter = null;
        if (input.CounterEvidence is not null)
        {
            if (input.CounterEvidence.Count > DiscussionEvidenceLimits.MaxInterpretationListItems)
            {
                normalized = null;
                errorReason = $"counterEvidence cannot have more than {DiscussionEvidenceLimits.MaxInterpretationListItems} items.";
                return false;
            }

            normCounter = new List<string>(input.CounterEvidence.Count);
            foreach (string item in input.CounterEvidence)
            {
                if (string.IsNullOrWhiteSpace(item))
                {
                    normalized = null;
                    errorReason = "counterEvidence items must not be blank.";
                    return false;
                }

                string cleaned = item.Normalize(NormalizationForm.FormC).Trim(' ', '\t');
                if (cleaned.Length > DiscussionEvidenceLimits.MaxInterpretationItemChars
                    || DiscussionText.IsInvalid(cleaned, allowTabLineBreaks: false))
                {
                    normalized = null;
                    errorReason = "counterEvidence item is too long or contains control characters.";
                    return false;
                }

                normCounter.Add(cleaned);
            }
        }

        string? normConclusion = null;
        if (input.Conclusion is not null)
        {
            normConclusion = input.Conclusion.Normalize(NormalizationForm.FormC).Trim();
            if (normConclusion.Length == 0
                || normConclusion.Length > DiscussionEvidenceLimits.MaxInterpretationConclusionChars
                || !DiscussionText.HasVisibleCharacter(normConclusion)
                || DiscussionText.IsInvalid(normConclusion, allowTabLineBreaks: true))
            {
                normalized = null;
                errorReason = $"conclusion must contain a visible character, contain no disallowed control characters, and be at most {DiscussionEvidenceLimits.MaxInterpretationConclusionChars} characters.";
                return false;
            }
        }

        var provided = new List<string>();
        if (normSummary is not null)
        {
            provided.Add("summary");
        }

        if (normDirection is not null)
        {
            provided.Add("direction");
        }

        if (normFindings is not null && normFindings.Count > 0)
        {
            provided.Add("findings");
        }

        if (normSupporting is not null && normSupporting.Count > 0)
        {
            provided.Add("supportingEvidence");
        }

        if (normCounter is not null && normCounter.Count > 0)
        {
            provided.Add("counterEvidence");
        }

        if (normConclusion is not null)
        {
            provided.Add("conclusion");
        }

        if (provided.Count == 0)
        {
            normalized = null;
            errorReason = "interpretation must contain at least one populated field.";
            return false;
        }

        normalized = new DiscussionInterpretation(
            normSummary,
            normDirection,
            normFindings,
            normSupporting,
            normCounter,
            normConclusion,
            provided);
        errorReason = null;
        return true;
    }

    /// <summary>
    /// Serializes an interpretation record into canonical JSON for byte accounting and wire output.
    /// </summary>
    public static string SerializeInterpretationCanonical(DiscussionInterpretation interpretation)
    {
        ArgumentNullException.ThrowIfNull(interpretation);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, CanonicalWriterOptions))
        {
            writer.WriteStartObject();
            if (interpretation.Summary is not null)
            {
                writer.WriteString("summary", interpretation.Summary);
            }

            if (interpretation.Direction is not null)
            {
                writer.WriteString("direction", interpretation.Direction);
            }

            if (interpretation.Findings is not null && interpretation.Findings.Count > 0)
            {
                writer.WriteStartArray("findings");
                foreach (string item in interpretation.Findings)
                {
                    writer.WriteStringValue(item);
                }

                writer.WriteEndArray();
            }

            if (interpretation.SupportingEvidence is not null && interpretation.SupportingEvidence.Count > 0)
            {
                writer.WriteStartArray("supportingEvidence");
                foreach (string item in interpretation.SupportingEvidence)
                {
                    writer.WriteStringValue(item);
                }

                writer.WriteEndArray();
            }

            if (interpretation.CounterEvidence is not null && interpretation.CounterEvidence.Count > 0)
            {
                writer.WriteStartArray("counterEvidence");
                foreach (string item in interpretation.CounterEvidence)
                {
                    writer.WriteStringValue(item);
                }

                writer.WriteEndArray();
            }

            if (interpretation.Conclusion is not null)
            {
                writer.WriteString("conclusion", interpretation.Conclusion);
            }

            if (interpretation.ProvidedFields is not null && interpretation.ProvidedFields.Count > 0)
            {
                writer.WriteStartArray("providedFields");
                foreach (string item in interpretation.ProvidedFields)
                {
                    writer.WriteStringValue(item);
                }

                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}

/// <summary>
/// Deterministic extractor that produces a normalized provenance object from tool execution results.
/// </summary>
public static class DiscussionProvenanceExtractor
{
    private static readonly HashSet<string> ExcludedParameterKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "symbol", "symbols", "ticker", "groupId", "timeFrame", "timeframe", "asOfDate", "startDate", "endDate", "allowUnregistered",
    };

    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string ExtractCanonicalJson(
        string tool,
        string canonicalArgumentsJson,
        string resultJson,
        string? normalizedAnalysisIntent,
        string attachedBy,
        DateTimeOffset createdUtc,
        string sha256,
        int resultBytes)
    {
        using JsonDocument argDoc = JsonDocument.Parse(string.IsNullOrWhiteSpace(canonicalArgumentsJson) ? "{}" : canonicalArgumentsJson);
        using JsonDocument resDoc = TryParseOrEmpty(resultJson);

        JsonElement argRoot = argDoc.RootElement;
        JsonElement resRoot = resDoc.RootElement;

        // 1. Symbol & Symbols
        List<string>? symbolsList = TryGetStringArray(resRoot, "symbols") ?? TryGetStringArray(argRoot, "symbols");
        string? singleSymbol = TryGetString(resRoot, "symbol") ?? TryGetString(argRoot, "symbol") ?? TryGetString(resRoot, "ticker") ?? TryGetString(argRoot, "ticker");
        string? groupId = TryGetString(argRoot, "groupId");

        if (symbolsList is null)
        {
            if (singleSymbol is not null)
            {
                symbolsList = new List<string> { singleSymbol };
            }
            else if (groupId is not null)
            {
                symbolsList = new List<string> { $"group:{groupId}" };
            }
            else
            {
                symbolsList = new List<string>();
            }
        }

        string? symbol = singleSymbol;
        if (symbol is null && symbolsList.Count == 1 && !symbolsList[0].StartsWith("group:", StringComparison.Ordinal))
        {
            symbol = symbolsList[0];
        }

        // 2. DataRange
        (string Start, string End)? dataRange = ExtractDataRange(resRoot, argRoot);

        // 3. AsOfDate
        string? asOfDate = TryGetString(resRoot, "asOfDate")
            ?? TryGetString(argRoot, "asOfDate")
            ?? TryGetString(resRoot, "endDate")
            ?? TryGetString(argRoot, "endDate")
            ?? TryGetString(resRoot, "latestTimestamp")
            ?? TryGetString(resRoot, "rangeEnd")
            ?? dataRange?.End;

        // 4. TimeFrame
        string? timeFrame = TryGetString(resRoot, "timeFrame")
            ?? TryGetString(argRoot, "timeFrame")
            ?? TryGetString(resRoot, "timeframe")
            ?? TryGetString(argRoot, "timeframe");

        // 5. Indicator & Indicators
        (string indicator, List<string> indicators) = ExtractIndicators(tool, argRoot, resRoot);

        // 6. Warnings
        List<string> warnings = ExtractWarnings(resRoot);

        // 7. Presence metadata
        bool hasAnalysisIntent = normalizedAnalysisIntent is not null;
        var extractedFields = new List<string>();
        if (symbol is not null) extractedFields.Add("symbol");
        if (symbolsList.Count > 0) extractedFields.Add("symbols");
        if (asOfDate is not null) extractedFields.Add("asOfDate");
        if (timeFrame is not null) extractedFields.Add("timeFrame");
        if (dataRange is not null) extractedFields.Add("dataRange");
        if (!string.IsNullOrEmpty(indicator)) extractedFields.Add("indicator");
        if (indicators.Count > 0) extractedFields.Add("indicators");

        // Check if parameters has properties
        bool hasParameters = false;
        if (argRoot.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty prop in argRoot.EnumerateObject())
            {
                if (!ExcludedParameterKeys.Contains(prop.Name))
                {
                    hasParameters = true;
                    break;
                }
            }
        }

        if (hasParameters) extractedFields.Add("parameters");
        if (hasAnalysisIntent) extractedFields.Add("analysisIntent");
        if (warnings.Count > 0) extractedFields.Add("warnings");

        // Serialize canonical JSON
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            writer.WriteStartObject();

            if (symbol is not null)
            {
                writer.WriteString("symbol", symbol);
            }
            else
            {
                writer.WriteNull("symbol");
            }

            writer.WriteStartArray("symbols");
            foreach (string s in symbolsList)
            {
                writer.WriteStringValue(s);
            }

            writer.WriteEndArray();

            if (asOfDate is not null)
            {
                writer.WriteString("asOfDate", asOfDate);
            }
            else
            {
                writer.WriteNull("asOfDate");
            }

            if (timeFrame is not null)
            {
                writer.WriteString("timeFrame", timeFrame);
            }
            else
            {
                writer.WriteNull("timeFrame");
            }

            if (dataRange is { } dr)
            {
                writer.WriteStartObject("dataRange");
                writer.WriteString("start", dr.Start);
                writer.WriteString("end", dr.End);
                writer.WriteEndObject();
            }
            else
            {
                writer.WriteNull("dataRange");
            }

            writer.WriteString("indicator", indicator);

            writer.WriteStartArray("indicators");
            foreach (string ind in indicators)
            {
                writer.WriteStringValue(ind);
            }

            writer.WriteEndArray();

            writer.WriteStartObject("parameters");
            if (argRoot.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty prop in argRoot.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (!ExcludedParameterKeys.Contains(prop.Name))
                    {
                        writer.WritePropertyName(prop.Name);
                        prop.Value.WriteTo(writer);
                    }
                }
            }

            writer.WriteEndObject();

            if (normalizedAnalysisIntent is not null)
            {
                writer.WriteString("analysisIntent", normalizedAnalysisIntent);
            }
            else
            {
                writer.WriteNull("analysisIntent");
            }

            writer.WriteStartArray("warnings");
            foreach (string w in warnings)
            {
                writer.WriteStringValue(w);
            }

            writer.WriteEndArray();

            writer.WriteBoolean("hasAnalysisIntent", hasAnalysisIntent);

            writer.WriteStartArray("extractedFields");
            foreach (string field in extractedFields)
            {
                writer.WriteStringValue(field);
            }

            writer.WriteEndArray();

            writer.WriteStartObject("source");
            writer.WriteString("origin", "server");
            writer.WriteString("tool", tool);
            writer.WritePropertyName("arguments");
            writer.WriteRawValue(canonicalArgumentsJson);
            writer.WriteString("sha256", sha256);
            writer.WriteString("attachedBy", attachedBy);
            writer.WriteString("createdUtc", DiscussionNames.FormatUtc(createdUtc));
            writer.WriteNumber("resultBytes", resultBytes);
            writer.WriteEndObject();

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static JsonDocument TryParseOrEmpty(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return JsonDocument.Parse("{}");
        }

        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return JsonDocument.Parse("{}");
        }
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out JsonElement prop) && prop.ValueKind == JsonValueKind.String)
        {
            string val = prop.GetString()!;
            if (!string.IsNullOrWhiteSpace(val))
            {
                return val;
            }
        }

        return null;
    }

    private static List<string>? TryGetStringArray(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out JsonElement prop) && prop.ValueKind == JsonValueKind.Array)
        {
            var list = new List<string>();
            foreach (JsonElement item in prop.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                {
                    list.Add(item.GetString()!);
                }
            }

            if (list.Count > 0)
            {
                return list;
            }
        }

        return null;
    }

    private static (string Start, string End)? ExtractDataRange(JsonElement resRoot, JsonElement argRoot)
    {
        if (TryGetString(resRoot, "earliestCalculatedTimestamp") is { } e && TryGetString(resRoot, "latestTimestamp") is { } l)
        {
            return (e, l);
        }

        if (TryGetString(resRoot, "rangeStart") is { } rs && TryGetString(resRoot, "rangeEnd") is { } re)
        {
            return (rs, re);
        }

        if (TryGetString(resRoot, "startDate") is { } sd && TryGetString(resRoot, "endDate") is { } ed)
        {
            return (sd, ed);
        }

        if (resRoot.ValueKind == JsonValueKind.Object && resRoot.TryGetProperty("dataRange", out JsonElement dr) && dr.ValueKind == JsonValueKind.Object)
        {
            string? start = TryGetString(dr, "start");
            string? end = TryGetString(dr, "end");
            if (start is not null && end is not null)
            {
                return (start, end);
            }
        }

        if (resRoot.ValueKind == JsonValueKind.Object && resRoot.TryGetProperty("candles", out JsonElement candles) && candles.ValueKind == JsonValueKind.Array && candles.GetArrayLength() > 0)
        {
            JsonElement first = candles[0];
            JsonElement last = candles[candles.GetArrayLength() - 1];
            string? firstTime = TryGetString(first, "timestamp") ?? TryGetString(first, "date");
            string? lastTime = TryGetString(last, "timestamp") ?? TryGetString(last, "date");
            if (firstTime is not null && lastTime is not null)
            {
                return (firstTime, lastTime);
            }
        }

        if (TryGetStringArray(resRoot, "dates") is { Count: > 0 } dates)
        {
            return (dates[0], dates[^1]);
        }

        if (resRoot.ValueKind == JsonValueKind.Object && resRoot.TryGetProperty("series", out JsonElement series) && series.ValueKind == JsonValueKind.Object)
        {
            if (TryGetStringArray(series, "dates") is { Count: > 0 } sdates)
            {
                return (sdates[0], sdates[^1]);
            }
        }

        if (TryGetString(argRoot, "startDate") is { } asd && TryGetString(argRoot, "endDate") is { } aed)
        {
            return (asd, aed);
        }

        return null;
    }

    private static (string Indicator, List<string> Indicators) ExtractIndicators(string tool, JsonElement argRoot, JsonElement resRoot)
    {
        var indicators = new List<string>();

        if (argRoot.ValueKind == JsonValueKind.Object && argRoot.TryGetProperty("indicators", out JsonElement inds) && inds.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in inds.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    string s = item.GetString()!;
                    if (!string.IsNullOrWhiteSpace(s))
                    {
                        indicators.Add(s);
                    }
                }
                else if (item.ValueKind == JsonValueKind.Object && TryGetString(item, "type") is { } t)
                {
                    indicators.Add(t);
                }
            }

            if (indicators.Count > 0)
            {
                string ind = indicators.Count == 1 ? indicators[0] : string.Join(",", indicators);
                return (ind, indicators);
            }
        }

        if (TryGetString(argRoot, "indicator") is { } indStr)
        {
            indicators.Add(indStr);
            return (indStr, indicators);
        }

        if (argRoot.ValueKind == JsonValueKind.Object && argRoot.TryGetProperty("indicator", out JsonElement indObj) && indObj.ValueKind == JsonValueKind.Object && TryGetString(indObj, "type") is { } indType)
        {
            indicators.Add(indType);
            return (indType, indicators);
        }

        if (TryGetString(resRoot, "indicator") is { } resInd)
        {
            indicators.Add(resInd);
            return (resInd, indicators);
        }

        return (tool, indicators);
    }

    private static List<string> ExtractWarnings(JsonElement resRoot)
    {
        var warnings = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        if (TryGetStringArray(resRoot, "warnings") is { } topWarnings)
        {
            foreach (string w in topWarnings)
            {
                if (seen.Add(w))
                {
                    warnings.Add(w);
                }
            }
        }

        if (resRoot.ValueKind == JsonValueKind.Object && resRoot.TryGetProperty("results", out JsonElement results) && results.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty indProp in results.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                if (indProp.Value.ValueKind == JsonValueKind.Object)
                {
                    if (TryGetStringArray(indProp.Value, "warnings") is { } indWarnings)
                    {
                        foreach (string w in indWarnings)
                        {
                            string formatted = $"{indProp.Name}: {w}";
                            if (seen.Add(formatted))
                            {
                                warnings.Add(formatted);
                            }
                        }
                    }

                    if (TryGetStringArray(indProp.Value, "nonCausalSeries") is { } nonCausal)
                    {
                        foreach (string s in nonCausal)
                        {
                            string formatted = $"{indProp.Name}: non-causal series '{s}'";
                            if (seen.Add(formatted))
                            {
                                warnings.Add(formatted);
                            }
                        }
                    }
                }
            }
        }

        if (resRoot.ValueKind == JsonValueKind.Object && resRoot.TryGetProperty("isNonCausal", out JsonElement inc) && inc.ValueKind == JsonValueKind.True)
        {
            const string ncWarning = "non-causal analysis output";
            if (seen.Add(ncWarning))
            {
                warnings.Add(ncWarning);
            }
        }

        return warnings;
    }
}
