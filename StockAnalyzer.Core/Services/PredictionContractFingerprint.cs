using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Training;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// A fingerprint of the prediction settings that decide whether a generation can be loaded and read: the window, the feature mode, the composed-feature
/// spec (only in composed mode) and the class labels. A recorded load failure, and a cached single-model attention context, are reused only while this
/// value is unchanged.
/// </summary>
public static class PredictionContractFingerprint
{
    /// <summary>Layout version; a change of the text layout below must change it.</summary>
    internal const string Version = "v1";

    private const char FieldSeparator = '\n';
    private const string LabelSeparator = ",";

    /// <summary>
    /// Lower-case hexadecimal SHA-256 of the UTF-8 text <c>v1 \n window \n mode \n spec \n labels</c> (window in the invariant culture, spec as the
    /// shared training JSON in composed mode and empty otherwise, labels joined by a comma).
    /// </summary>
    public static string Compute(IStockAnalyzerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string spec = settings.PredictionFeatureMode == PredictionFeatureMode.ComposedFeatures
            ? JsonSerializer.Serialize(settings.PredictionFeatureSpec, TrainingConfigJson.Options)
            : "";
        string labels = string.Join(LabelSeparator, settings.PredictionClassLabels);
        string text = string.Join(FieldSeparator, Version, settings.PredictionWindowSize.ToString(CultureInfo.InvariantCulture),
            settings.PredictionFeatureMode.ToString(), spec, labels);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }
}
