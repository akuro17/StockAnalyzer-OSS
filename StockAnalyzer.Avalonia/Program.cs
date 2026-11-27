using Avalonia;
using Avalonia.Controls;
using Avalonia.ReactiveUI;
using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Serilog;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Formatting.Compact;
using StockAnalyzer.Core.Models.Settings;

namespace StockAnalyzer.Avalonia;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Force English culture as primary language of StockAnalyzer
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");

        // Global crash logger for unhandled exceptions
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            var ex = (Exception)e.ExceptionObject;
            var msg = $"Unhandled: {ex}";
            Console.Error.WriteLine(msg);
        };
        TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            var msg = $"Unobserved: {e.Exception}";
            Console.Error.WriteLine(msg);
            e.SetObserved();
        };

        // Configure Serilog before anything else (Default OFF)
        var logDirectory = System.IO.Path.Combine(AppContext.BaseDirectory, "logs");
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.ControlledBy(Services.LogService.LevelSwitch)
            .WriteTo.Async(a => a.Console(outputTemplate: "{Timestamp:HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}"))
            .WriteTo.Async(a => a.File(
                new CompactJsonFormatter(),
                path: System.IO.Path.Combine(logDirectory, "stockanalyzer-json-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30))
            .WriteTo.Async(a => a.File(
                path: System.IO.Path.Combine(logDirectory, "stockanalyzer-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}"))
            .CreateLogger();

        // Global exception handler for unhandled exceptions
        AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
        {
            Log.Fatal(e.ExceptionObject as Exception, "Unhandled exception in AppDomain");
            Log.CloseAndFlush();
        };

        try
        {
            Log.Information("Application starting...");
            BuildAvaloniaApp()
                .StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Application crashed");
            var msg = $"Launcher Error: {ex.Message}\n{ex.StackTrace}";
            var sanitizedMsg = StockAnalyzer.Core.Common.ExceptionSanitizer.Sanitize(msg);
            try
            {
                System.IO.File.WriteAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "launcher_error.log"), sanitizedMsg);
            }
            catch
            {
                try
                {
                    var fallbackPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "StockAnalyzer_launcher_error.log");
                    System.IO.File.WriteAllText(fallbackPath, sanitizedMsg);
                }
                catch
                {
                    Console.Error.WriteLine(sanitizedMsg);
                }
            }
            throw;
        }
        finally
        {
            Log.Information("Application shutting down");
            Log.CloseAndFlush();
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .With(new SkiaOptions
            {
                MaxGpuResourceSizeBytes = ResolveMaxGpuResourceSizeBytes()
            });

        if (OperatingSystem.IsWindows())
        {
            builder = builder.With(new Win32PlatformOptions
            {
                RenderingMode = new[]
                {
                    Win32RenderingMode.AngleEgl,
                    Win32RenderingMode.Vulkan,
                    Win32RenderingMode.Wgl,
                    Win32RenderingMode.Software
                },
                CompositionMode = new[]
                {
                    Win32CompositionMode.WinUIComposition,
                    Win32CompositionMode.DirectComposition
                }
            });
        }

        return builder;
    }

    /// <summary>
    /// Reads <c>Rendering:MaxGpuResourceSizeBytes</c> prior to DI construction.
    /// Falls back silently to <see cref="RenderingLimits.DefaultMaxGpuResourceSizeBytes"/> (128 MiB) during design mode,
    /// when <c>appsettings.json</c> is absent/corrupt, or when the configured value is outside the hard band.
    /// </summary>
    internal static long ResolveMaxGpuResourceSizeBytes(string? appsettingsPath = null, bool? isDesignModeOverride = null)
    {
        if (isDesignModeOverride ?? Design.IsDesignMode)
        {
            return RenderingLimits.DefaultMaxGpuResourceSizeBytes;
        }

        string path = string.IsNullOrWhiteSpace(appsettingsPath)
            ? Path.Combine(AppContext.BaseDirectory, "appsettings.json")
            : appsettingsPath;

        if (!File.Exists(path))
        {
            return RenderingLimits.DefaultMaxGpuResourceSizeBytes;
        }

        try
        {
            using var stream = File.OpenRead(path);
            using var doc = JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });

            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty(RenderingSettings.SectionName, out var renderingSection)
                && renderingSection.ValueKind == JsonValueKind.Object
                && renderingSection.TryGetProperty(nameof(RenderingSettings.MaxGpuResourceSizeBytes), out var bytesElement)
                && bytesElement.ValueKind == JsonValueKind.Number
                && bytesElement.TryGetInt64(out long configuredBytes))
            {
                // Enforce the hard band identically to DI startup validation (no silent fallback on an explicit out-of-band value).
                _ = RenderingSettingsValidator.Validate(configuredBytes);
                return configuredBytes;
            }
        }
        catch (ArgumentOutOfRangeException)
        {
            throw;
        }
        catch (Exception)
        {
            // Never fail app startup or visual designer on malformed pre-DI settings JSON.
        }

        return RenderingLimits.DefaultMaxGpuResourceSizeBytes;
    }
}
