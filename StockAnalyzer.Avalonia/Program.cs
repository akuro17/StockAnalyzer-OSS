using Avalonia;
using Avalonia.ReactiveUI;
using System;
using System.Threading.Tasks;
using Serilog;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Formatting.Compact;

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

        // Crash loggers for Y:\Temp
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
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.ControlledBy(Services.LogService.LevelSwitch)
            .WriteTo.Async(a => a.Console(outputTemplate: "{Timestamp:HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}"))
            .WriteTo.Async(a => a.File(
                new CompactJsonFormatter(),
                path: "logs/stockanalyzer-json-.log",
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30))
            .WriteTo.Async(a => a.File(
                path: "logs/stockanalyzer-.log",
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
            Console.WriteLine(msg);
            System.IO.File.WriteAllText("launcher_error.log", msg);
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
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
