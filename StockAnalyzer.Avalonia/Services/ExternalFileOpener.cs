using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace StockAnalyzer.Avalonia.Services;

/// <summary>
/// Default implementation of <see cref="IExternalFileOpener"/> using OS shell execution and native launchers.
/// </summary>
public class ExternalFileOpener : IExternalFileOpener
{
    private readonly ILogger<ExternalFileOpener> _logger;

    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".svg", ".ico",
        ".pdf", ".txt", ".csv", ".parquet", ".json", ".xml", ".log"
    };

    public ExternalFileOpener(ILogger<ExternalFileOpener>? logger = null)
    {
        _logger = logger ?? NullLogger<ExternalFileOpener>.Instance;
    }

    public bool OpenFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            _logger.LogWarning("File open requested with empty path.");
            return false;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(filePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Invalid file path format: {FilePath}", filePath);
            return false;
        }

        if (!File.Exists(fullPath))
        {
            _logger.LogWarning("Target file does not exist: {FilePath}", fullPath);
            return false;
        }

        string ext = Path.GetExtension(fullPath);
        if (!AllowedExtensions.Contains(ext))
        {
            _logger.LogWarning("Attempted to open file with unapproved extension: {Extension} ({FilePath})", ext, fullPath);
            return false;
        }

        try
        {
            var hostPlatform = DetectHostPlatform();
            var startInfo = CreateFileProcessStartInfo(fullPath, hostPlatform);
            Process.Start(startInfo);
            return true;
        }
        catch (Win32Exception ex) when (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Direct ShellExecute on Windows can fail with Win32Exception (e.g. error 1155 ERROR_NO_ASSOCIATION)
            // when the default file handler is a packaged/UWP app (such as the Windows Photos app, dotnet/runtime #28005).
            // Fallback to explorer.exe, which resolves the OS file association via Windows Shell activation.
            _logger.LogInformation(ex, "Direct shell execution for {FilePath} failed (NativeErrorCode={ErrorCode}). Falling back to explorer.exe launcher.", fullPath, ex.NativeErrorCode);
            return TryOpenViaExplorer(fullPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to open file at {FilePath}.", fullPath);
            return false;
        }
    }

    public bool OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            _logger.LogWarning("OpenUrl requested with empty URL.");
            return false;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            _logger.LogWarning("Invalid or non-HTTP(S) URL rejected: {Url}", url);
            return false;
        }

        try
        {
            var hostPlatform = DetectHostPlatform();
            var startInfo = CreateUrlProcessStartInfo(url, hostPlatform);
            Process.Start(startInfo);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to open URL {Url}.", url);
            return false;
        }
    }

    internal static ProcessStartInfo CreateFileProcessStartInfo(string filePath, OSPlatform hostPlatform)
    {
        if (hostPlatform == OSPlatform.Windows)
        {
            // Windows: Direct shell association resolution without intermediate explorer.exe
            return new ProcessStartInfo
            {
                FileName = filePath,
                UseShellExecute = true
            };
        }

        if (hostPlatform == OSPlatform.OSX)
        {
            var startInfo = new ProcessStartInfo("open") { UseShellExecute = false };
            startInfo.ArgumentList.Add(filePath);
            return startInfo;
        }

        // Linux and other Unix-like systems
        var linuxStartInfo = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
        linuxStartInfo.ArgumentList.Add(filePath);
        return linuxStartInfo;
    }

    internal static ProcessStartInfo CreateUrlProcessStartInfo(string url, OSPlatform hostPlatform)
    {
        if (hostPlatform == OSPlatform.Windows)
        {
            return new ProcessStartInfo(url) { UseShellExecute = true };
        }

        if (hostPlatform == OSPlatform.OSX)
        {
            var startInfo = new ProcessStartInfo("open") { UseShellExecute = false };
            startInfo.ArgumentList.Add(url);
            return startInfo;
        }

        var linuxStartInfo = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
        linuxStartInfo.ArgumentList.Add(url);
        return linuxStartInfo;
    }

    private bool TryOpenViaExplorer(string filePath)
    {
        try
        {
            var psi = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            psi.ArgumentList.Add(filePath);
            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to open file via explorer.exe fallback for {FilePath}.", filePath);
            return false;
        }
    }

    private static OSPlatform DetectHostPlatform()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return OSPlatform.Windows;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return OSPlatform.OSX;
        return OSPlatform.Linux;
    }
}
