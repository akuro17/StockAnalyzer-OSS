namespace StockAnalyzer.Core.Models;

/// <summary>
/// Selects the feature normalization strategy used to build ONNX model input tensors.
/// </summary>
public enum PredictionFeatureMode : byte
{
    /// <summary>OHLCV min-max normalization (5 features per bar).</summary>
    OhlcvMinMax = 0,

    /// <summary>Log-return of close price (1 feature per bar).</summary>
    LogReturn = 1,

    /// <summary>Population Z-Score standardized OHLCV, per channel (5 features per bar).</summary>
    ZScoreStandardized = 2,

    /// <summary>
    /// Z-Score standardized OHLCV where O/H/L/C share one pooled mean/std over all four
    /// price channels (Volume standardized separately); preserves candle geometry.
    /// 5 features per bar. Wire string: <c>zscore_joint</c>.
    /// </summary>
    ZScoreOhlcvJoint = 3,

    /// <summary>
    /// Intrabar OHLC log-returns: <c>ln(Open/PrevClose)</c> (gap), <c>ln(High/Open)</c>,
    /// <c>ln(Low/Open)</c>, <c>ln(Close/Open)</c>. 4 features per bar; no Volume.
    /// Wire string: <c>log_return_ohlc</c>.
    /// </summary>
    LogReturnOhlc = 4,
}
