using Avalonia;
using Avalonia.Headless;
using StockAnalyzer.Avalonia.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace StockAnalyzer.Avalonia.Tests;

// Configures the Application used by [AvaloniaFact] tests. Without this, Avalonia.Headless
// falls back to a bare Application with no styles/themes registered, so TemplatedControls
// (TreeView, TreeViewItem, etc.) never resolve a ControlTemplate and stay visually empty -
// which breaks any test that needs to interact with realized item containers.
// Reuses the real StockAnalyzer.Avalonia.App (its Initialize() only loads App.axaml's
// resources/styles via AvaloniaXamlLoader; it does not run the desktop-lifetime DI bootstrap
// in OnFrameworkInitializationCompleted, since that path is gated on
// IClassicDesktopStyleApplicationLifetime, which headless mode never provides).
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<StockAnalyzer.Avalonia.App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
