using System;
using System.Collections.Generic;

namespace StockAnalyzer.Core.Constants;

/// <summary>
/// Centralized vocabulary of ParameterTagAttribute values for chart drawing tools.
/// Single Source of Truth (SSoT) for tag literals shared between drawing object classes
/// (which declare the tag) and drawing UI-building code (which filters on it).
/// </summary>
public static class DrawingParameterTags
{
    /// <summary>
    /// Infrastructure properties that must be ignored during UI parameter generation and state tracking.
    /// Single Source of Truth (SSoT) shared across ViewBuilders and ViewModels.
    /// </summary>
    public static readonly HashSet<string> IgnoredPropertyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Id",
        "Points",
        "IsSelected",
        "SkiaColor"
    };

    /// <summary>Core properties applicable to all drawing objects (Color, Thickness, ZIndex, IsLocked, IsVisible, MoveAxisMode).</summary>
    public const string Common = "Common";

    /// <summary>Fill properties (IsFilled, FillColor, FillOpacity, GradientType, BlendMode).</summary>
    public const string Fill = "Fill";

    /// <summary>Text and label properties (Text, FontSize, TextColor, TextBackgroundColor, TextAlignment).</summary>
    public const string Typography = "Typography";

    /// <summary>Coordinate / point / anchor configurations.</summary>
    public const string Geometry = "Geometry";

    /// <summary>Analytical projection parameters (DTW, Kalman, ARIMA, FFT, HMM, Pearson, Frechet, SSA).</summary>
    public const string Projection = "Projection";

    /// <summary>Position sizing, stop, target, and box parameters (Long/Short, Risk/Reward).</summary>
    public const string RiskReward = "RiskReward";

    /// <summary>Pattern degree, tolerance, harmonic/wave settings.</summary>
    public const string Analysis = "Analysis";

    /// <summary>Volume profile row count, value area, up/down bar colors.</summary>
    public const string VolumeProfile = "VolumeProfile";
}
