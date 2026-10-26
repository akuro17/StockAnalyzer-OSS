using System.Text.Json;
using StockAnalyzer.Core.Execution;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>The one definition of the training request's argument names: sa_start_training reads them, sa_get_training_status echoes them back.</summary>
internal static class TrainingRequestJson
{
    public const string SymbolsArgument = "symbols";
    public const string StartDateArgument = "startDate";
    public const string EndDateArgument = "endDate";
    public const string FrameworkArgument = "framework";
    public const string ArchitectureArgument = "architecture";
    public const string WindowSizeArgument = "windowSize";
    public const string HorizonArgument = "horizon";
    public const string TargetTypeArgument = "targetType";
    public const string HyperparametersArgument = "hyperparameters";
    public const string SplitsArgument = "nSplits";
    public const string FixedZScoreArgument = "fixedZScore";
    public const string ClipSigmaArgument = "clipSigma";
    public const string CalibrationArgument = "calibration";
    public const string CpcvArgument = "cpcv";

    /// <summary>Writes the request as the arguments that would start the same job.</summary>
    public static void Write(Utf8JsonWriter writer, EphemeralTrainingRequest request)
    {
        writer.WriteStartObject();
        writer.WriteStartArray(SymbolsArgument);
        foreach (string symbol in request.Symbols)
        {
            writer.WriteStringValue(symbol);
        }

        writer.WriteEndArray();
        WriteOptionalDate(writer, StartDateArgument, request.StartDate);
        WriteOptionalDate(writer, EndDateArgument, request.EndDate);
        writer.WriteString(FrameworkArgument, request.Framework.ToString());
        writer.WriteString(ArchitectureArgument, request.Architecture);
        writer.WriteNumber(WindowSizeArgument, request.WindowSize);
        writer.WriteNumber(HorizonArgument, request.Horizon);
        writer.WriteString(TargetTypeArgument, request.TargetType.ToString());
        writer.WriteNumber(SplitsArgument, request.NSplits);
        if (request.FixedZScore)
        {
            writer.WriteBoolean(FixedZScoreArgument, true);
        }

        if (request.ClipSigma is { } clipSigma)
        {
            writer.WriteNumber(ClipSigmaArgument, clipSigma);
        }

        if (request.Calibration)
        {
            writer.WriteBoolean(CalibrationArgument, true);
        }

        if (request.Cpcv)
        {
            writer.WriteBoolean(CpcvArgument, true);
        }

        writer.WriteStartObject(HyperparametersArgument);
        foreach (KeyValuePair<string, string> pair in request.Hyperparameters.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            writer.WriteString(pair.Key, pair.Value);
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteOptionalDate(Utf8JsonWriter writer, string name, DateOnly? date)
    {
        if (date is { } value)
        {
            writer.WriteString(name, value.ToString(ToolJson.DateFormat, System.Globalization.CultureInfo.InvariantCulture));
        }
    }
}
