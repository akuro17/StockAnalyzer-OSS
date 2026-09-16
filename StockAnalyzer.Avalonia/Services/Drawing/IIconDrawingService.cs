using System.Collections.Generic;
using SkiaSharp;

namespace StockAnalyzer.Avalonia.Services.Drawing;

public sealed record IconGroupInfo(string Key, string DisplayName, bool IsAll);

public sealed record IconItemInfo(string RelativePath, string FullPath, string Name, string GroupKey);

/// <summary>
/// Service contract for discovering, categorizing, and caching icons from Data\Drawings\Icon.
/// Supports strictly 4 formats: .ico, .icns, .png, .svg.
/// </summary>
public interface IIconDrawingService
{
    IReadOnlyList<IconGroupInfo> GetGroups();

    IReadOnlyList<IconItemInfo> GetIcons(string? groupKey = null);

    IconItemInfo? ActiveIcon { get; set; }

    SKBitmap? GetSkiaBitmap(string relativeOrFullPath);

    global::Avalonia.Media.Imaging.Bitmap? GetAvaloniaBitmap(string relativeOrFullPath);

    void Refresh();
}
