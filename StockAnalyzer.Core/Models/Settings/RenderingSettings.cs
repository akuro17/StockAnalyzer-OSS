using System.Collections.Generic;

namespace StockAnalyzer.Core.Models.Settings;

/// <summary>
/// Configuration bound from the <c>Rendering</c> section of <c>appsettings.json</c> (cross-platform Skia options).
/// Validated against <see cref="RenderingLimits"/> at startup by <see cref="RenderingSettingsValidator"/>.
/// </summary>
public class RenderingSettings
{
    public const string SectionName = "Rendering";

    public long MaxGpuResourceSizeBytes { get; set; } = RenderingLimits.DefaultMaxGpuResourceSizeBytes;
}

/// <summary>
/// Validates <see cref="RenderingSettings"/> against <see cref="RenderingLimits"/>.
/// </summary>
public static class RenderingSettingsValidator
{
    public const string MaxGpuResourceSizeBytesKey = RenderingSettings.SectionName + ":MaxGpuResourceSizeBytes";

    public static IReadOnlyList<string> Validate(RenderingSettings settings)
    {
        System.ArgumentNullException.ThrowIfNull(settings);
        return Validate(settings.MaxGpuResourceSizeBytes);
    }

    public static IReadOnlyList<string> Validate(long maxGpuResourceSizeBytes)
    {
        var warnings = new List<string>();
        SettingBandValidation.Check(
            MaxGpuResourceSizeBytesKey,
            nameof(maxGpuResourceSizeBytes),
            RenderingLimits.MaxGpuResourceSizeBytes,
            maxGpuResourceSizeBytes,
            "bytes",
            "small Skia caches may evict chart textures frequently on 4K displays, while very large caches reserve more GPU/shared RAM",
            warnings);
        return warnings;
    }
}
