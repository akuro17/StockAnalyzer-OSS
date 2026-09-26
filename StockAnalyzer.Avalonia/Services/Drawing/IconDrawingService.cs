using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using StockAnalyzer.Core.Common;

namespace StockAnalyzer.Avalonia.Services.Drawing;

/// <summary>
/// Service managing icon discovery from Data\Drawings\Icon, group categorization (root + 1st level subfolders),
/// and thread-safe decoding and caching for .ico, .icns, .png, .svg.
/// </summary>
public class IconDrawingService : IIconDrawingService
{
    public const string AllGroupKey = "ALL";

    public static readonly string[] SupportedExtensions = [".ico", ".icns", ".png", ".svg"];

    private readonly ILogger<IconDrawingService> _logger;
    private readonly string? _customRootDir;
    private readonly ConcurrentDictionary<string, (SKBitmap? Skia, global::Avalonia.Media.Imaging.Bitmap? Avalonia)> _imageCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly object _lock = new();
    private List<IconGroupInfo> _groups = new();
    private List<IconItemInfo> _allIcons = new();
    private bool _isInitialized;

    public IconItemInfo? ActiveIcon { get; set; }

    public IconDrawingService(ILogger<IconDrawingService>? logger = null, string? rootDirectory = null)
    {
        _logger = logger ?? NullLogger<IconDrawingService>.Instance;
        _customRootDir = rootDirectory;
    }

    private void EnsureInitialized()
    {
        if (_isInitialized) return;
        lock (_lock)
        {
            if (_isInitialized) return;
            ScanIcons();
            _isInitialized = true;
        }
    }

    public void Refresh()
    {
        lock (_lock)
        {
            _imageCache.Clear();
            ScanIcons();
            _isInitialized = true;
        }
    }

    public IReadOnlyList<IconGroupInfo> GetGroups()
    {
        EnsureInitialized();
        return _groups;
    }

    public IReadOnlyList<IconItemInfo> GetIcons(string? groupKey = null)
    {
        EnsureInitialized();

        if (string.IsNullOrEmpty(groupKey) || groupKey == AllGroupKey)
        {
            return _allIcons;
        }

        return _allIcons.Where(i => string.Equals(i.GroupKey, groupKey, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public SKBitmap? GetSkiaBitmap(string relativeOrFullPath)
    {
        var fullPath = ResolveFullPath(relativeOrFullPath);
        if (string.IsNullOrEmpty(fullPath) || !File.Exists(fullPath)) return null;

        var cached = _imageCache.GetOrAdd(fullPath, LoadImagePair);
        return cached.Skia;
    }

    public global::Avalonia.Media.Imaging.Bitmap? GetAvaloniaBitmap(string relativeOrFullPath)
    {
        var fullPath = ResolveFullPath(relativeOrFullPath);
        if (string.IsNullOrEmpty(fullPath) || !File.Exists(fullPath)) return null;

        var cached = _imageCache.GetOrAdd(fullPath, LoadImagePair);
        return cached.Avalonia;
    }

    private string GetRootDirectory() => _customRootDir ?? PathDiscovery.ResolveDataPath(null, "Data/Drawings/Icon");

    private string ResolveFullPath(string relativeOrFullPath)
    {
        if (Path.IsPathRooted(relativeOrFullPath)) return relativeOrFullPath;

        var rootDir = GetRootDirectory();
        return Path.Combine(rootDir, relativeOrFullPath);
    }

    private (SKBitmap? Skia, global::Avalonia.Media.Imaging.Bitmap? Avalonia) LoadImagePair(string fullPath)
    {
        if (!File.Exists(fullPath)) return (null, null);

        var ext = Path.GetExtension(fullPath);
        if (string.IsNullOrEmpty(ext) || !SupportedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
        {
            return (null, null);
        }

        try
        {
            SKBitmap? skBitmap = null;

            if (ext.Equals(".svg", StringComparison.OrdinalIgnoreCase))
            {
                skBitmap = SvgIconDecoder.RenderToSkBitmap(fullPath);
            }
            else if (ext.Equals(".icns", StringComparison.OrdinalIgnoreCase))
            {
                var pngBytes = IcnsDecoder.ExtractLargestPng(fullPath);
                if (pngBytes != null && pngBytes.Length > 0)
                {
                    using var ms = new MemoryStream(pngBytes);
                    skBitmap = SKBitmap.Decode(ms);
                }
            }
            else // .png, .ico
            {
                skBitmap = SKBitmap.Decode(fullPath);
            }

            if (skBitmap == null) return (null, null);

            // Create Avalonia Bitmap from encoded PNG of the SKBitmap
            global::Avalonia.Media.Imaging.Bitmap? avaloniaBitmap = null;
            using (var image = SKImage.FromBitmap(skBitmap))
            using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
            using (var ms = new MemoryStream())
            {
                data.SaveTo(ms);
                ms.Position = 0;
                avaloniaBitmap = new global::Avalonia.Media.Imaging.Bitmap(ms);
            }

            return (skBitmap, avaloniaBitmap);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to decode icon at {FullPath}", fullPath);
            return (null, null);
        }
    }

    private void ScanIcons()
    {
        var rootDir = GetRootDirectory();
        try
        {
            if (!Directory.Exists(rootDir))
            {
                Directory.CreateDirectory(rootDir);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create or verify icon root directory at {RootDir}", rootDir);
            _groups = new List<IconGroupInfo> { new(AllGroupKey, "All Icons", true) };
            _allIcons = new List<IconItemInfo>();
            return;
        }

        var groups = new List<IconGroupInfo>();
        var icons = new List<IconItemInfo>();

        var allDisplayName = LocalizationManager.Instance["Icon_AllIcons"];
        if (string.IsNullOrWhiteSpace(allDisplayName) || allDisplayName.StartsWith("["))
        {
            allDisplayName = "すべてのアイコン";
        }
        groups.Add(new IconGroupInfo(AllGroupKey, allDisplayName, true));

        // 1. Root level files
        try
        {
            var rootFiles = Directory.EnumerateFiles(rootDir)
                .Where(f => SupportedExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase);

            foreach (var file in rootFiles)
            {
                var fileName = Path.GetFileNameWithoutExtension(file);
                var relPath = Path.GetFileName(file);
                icons.Add(new IconItemInfo(relPath, file, fileName, AllGroupKey));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enumerate root icon files in {RootDir}", rootDir);
        }

        // 2. 1st-level subdirectories only (ignore deeper directories)
        try
        {
            var subDirs = Directory.EnumerateDirectories(rootDir)
                .OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase);

            foreach (var subDir in subDirs)
            {
                var folderName = Path.GetFileName(subDir);
                if (string.IsNullOrWhiteSpace(folderName)) continue;

                var subFiles = Directory.EnumerateFiles(subDir)
                    .Where(f => SupportedExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                    .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (subFiles.Count > 0)
                {
                    groups.Add(new IconGroupInfo(folderName, folderName, false));

                    foreach (var file in subFiles)
                    {
                        var fileName = Path.GetFileNameWithoutExtension(file);
                        var relPath = Path.Combine(folderName, Path.GetFileName(file));
                        icons.Add(new IconItemInfo(relPath, file, fileName, folderName));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enumerate icon subdirectories in {RootDir}", rootDir);
        }

        _groups = groups;
        _allIcons = icons;

        if (ActiveIcon != null && !_allIcons.Any(i => string.Equals(i.RelativePath, ActiveIcon.RelativePath, StringComparison.OrdinalIgnoreCase)))
        {
            ActiveIcon = null;
        }
    }
}
