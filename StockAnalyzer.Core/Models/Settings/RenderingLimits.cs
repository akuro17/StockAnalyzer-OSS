namespace StockAnalyzer.Core.Models.Settings;

/// <summary>
/// System contract of cross-platform Skia rendering limits (section <c>Rendering</c>, numeric classification).
/// Platform-specific backend/composition options (e.g. Win32) remain independent of these bounds.
/// </summary>
public static class RenderingLimits
{
    /// <summary>Default Skia GPU resource cache budget (128 MiB), balanced for 16 GB shared-memory iGPUs and 4K displays.</summary>
    public const long DefaultMaxGpuResourceSizeBytes = 134_217_728L;

    private const long Mebibyte = 1024L * 1024L;

    /// <summary>
    /// Hard band [32 MiB, 512 MiB] rejects extreme GPU cache settings; standard band [64 MiB, 256 MiB] runs without warning.
    /// </summary>
    public static readonly SettingBand MaxGpuResourceSizeBytes = new(
        32L * Mebibyte,
        512L * Mebibyte,
        64L * Mebibyte,
        256L * Mebibyte);
}
