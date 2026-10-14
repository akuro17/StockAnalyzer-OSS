namespace StockAnalyzer.Core.Models.Training;

/// <summary>
/// Safety ceilings for the sidecar files that sit next to an ONNX model and are read whole into memory. They are a code contract, not configuration:
/// no settings file can widen them, because their job is to keep a foreign or damaged sidecar from exhausting memory.
/// </summary>
public static class ModelSidecarLimits
{
    /// <summary>
    /// Budget for one expanded channel of a fixed-scaler sidecar: four statistics at their longest round-trip text (about 24 characters each plus the
    /// key names, roughly 150 bytes) and the channel identity string, with room for identities of several hundred characters.
    /// </summary>
    public const int ScalerBytesPerChannel = 1024;

    /// <summary>Budget for everything that is not per channel: schema version, feature mode, window size, up to <see cref="TrainingResourceOverrides.MaximumFeatureLag"/> lags, clip sigma and the contract hash.</summary>
    public const int ScalerFixedBytes = 64 * 1024;

    /// <summary>
    /// The largest accepted fixed-scaler sidecar: <see cref="FeatureSpec.MaxChannels"/> channels at <see cref="ScalerBytesPerChannel"/> plus the fixed part.
    /// A test serializes a worst-case scaler (all channels, 256-character identities, longest doubles, all lags) and requires this to be at least twice its size.
    /// </summary>
    public const long MaxFixedScalerBytes = (long)FeatureSpec.MaxChannels * ScalerBytesPerChannel + ScalerFixedBytes;
}
