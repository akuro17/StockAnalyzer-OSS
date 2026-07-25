using Avalonia.Styling;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using System.Linq;
using Avalonia.Markup.Xaml;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using StockAnalyzer.Core.Theme;
using System;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using System.Threading.Tasks;

namespace StockAnalyzer.Avalonia;

/// <summary>
/// Application entry point.
/// Manages the DI Container and ensures proper resource disposal on exit.
/// </summary>
public partial class App : Application, IDisposable, IThemeVariantDispatcher
{
    private bool _disposed;
    private Window? _pythonProgressWindow;
    private TextBlock? _pythonProgressTextBlock;
    private IProgress<string>? _currentPythonProgress;

    private void OnPythonProgressChanged(object? sender, string e)
    {
        global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_pythonProgressTextBlock != null)
            {
                _pythonProgressTextBlock.Text = e;
            }
        });
    }

    /// <summary>
    /// Gets the current <see cref="App"/> instance in use
    /// </summary>
    public new static App Current => (App)Application.Current!;

    /// <summary>
    /// Gets the <see cref="IServiceProvider"/> instance to resolve application services.
    /// </summary>
    public IServiceProvider? Services { get; private set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        ActualThemeVariantChanged += OnRequestedThemeVariantChanged;
        try
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                // 1. Load configuration for Localization and DI
                var config = new ConfigurationBuilder()
                    .SetBasePath(AppContext.BaseDirectory)
                    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
                    .Build();
                
                var localePath = config["Localization:ResourcePath"];
                LocalizationManager.Instance.Initialize("en", localePath);

                // 2. Ensure DI is built
                if (Services == null)
                {
                    var services = new ServiceCollection();
                    services.AddCommonServices(config);
                    services.AddViewModels();
                    services.AddSingleton<IThemeVariantDispatcher>(this);
                    Services = services.BuildServiceProvider();
                    
                    StockAnalyzer.Avalonia.ViewModels.Watchlist.WatchlistItemViewModel.DispatcherService = Services.GetRequiredService<StockAnalyzer.Core.Services.IDispatcherService>();

                    // Configure Python setup decision hook
                    var pythonService = Services.GetRequiredService<StockAnalyzer.Core.Services.IPythonService>();
                    var dialogService = Services.GetRequiredService<IDialogService>();
                    pythonService.SetupDecisionProvider = async () =>
                    {
                        var decision = await dialogService.ShowPythonSetupConfirmationAsync();
                        if (decision == StockAnalyzer.Core.Services.PythonSetupDecision.Manual)
                        {
                            await dialogService.ShowManualSetupInstructionsAsync();
                        }
                        return decision;
                    };

                    pythonService.UpdateDecisionProvider = async () =>
                    {
                        var decision = await dialogService.ShowPythonUpdateConfirmationAsync();
                        if (decision == StockAnalyzer.Core.Services.PythonSetupDecision.Manual)
                        {
                            await dialogService.ShowPythonManualUpdateInstructionsAsync();
                        }
                        return decision;
                    };

                    pythonService.SetupProgressStarted = (progress) =>
                    {
                        global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        {
                            var desktop = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
                            if (desktop == null) return;

                            var bgSecondaryBrush = Application.Current!.FindResource("Brush.Background.Secondary") as IBrush ?? Brushes.Black;
                            var textPrimaryBrush = Application.Current!.FindResource("Brush.Text.Primary") as IBrush ?? Brushes.White;
                            var borderPrimaryBrush = Application.Current!.FindResource("Brush.Border.Primary") as IBrush ?? Brushes.DarkGray;

                            _pythonProgressTextBlock = new TextBlock 
                            { 
                                Text = "Initializing...", 
                                TextWrapping = TextWrapping.Wrap,
                                Foreground = textPrimaryBrush,
                                FontSize = 14,
                                HorizontalAlignment = HorizontalAlignment.Center
                            };

                            var progressBar = new ProgressBar
                            {
                                IsIndeterminate = true,
                                Height = 4,
                                Margin = new Thickness(0, 10, 0, 0),
                                Background = Brushes.Gray,
                                Foreground = Application.Current!.FindResource("Brush_ActionPrimary") as IBrush ?? Brushes.DodgerBlue
                            };

                            _pythonProgressWindow = new Window
                            {
                                Title = "Python Setup Progress",
                                MinWidth = 400,
                                MinHeight = 120,
                                SizeToContent = SizeToContent.WidthAndHeight,
                                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                                CanResize = false,
                                Background = bgSecondaryBrush,
                                Foreground = textPrimaryBrush,
                                Icon = desktop.MainWindow?.Icon,
                                Content = new Border
                                {
                                    BorderBrush = borderPrimaryBrush,
                                    BorderThickness = new Thickness(1),
                                    Child = new StackPanel
                                    {
                                        Margin = new Thickness(24),
                                        Spacing = 16,
                                        Children = { _pythonProgressTextBlock, progressBar }
                                    }
                                }
                            };

                            _currentPythonProgress = progress;
                            if (progress is Progress<string> pObj)
                            {
                                pObj.ProgressChanged += OnPythonProgressChanged;
                            }

                            if (desktop.MainWindow != null)
                            {
                                _pythonProgressWindow.Show(desktop.MainWindow);
                            }
                            else
                            {
                                _pythonProgressWindow.Show();
                            }
                        });
                    };

                    pythonService.SetupProgressFinished = () =>
                    {
                        global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        {
                            try
                            {
                                if (_currentPythonProgress is Progress<string> pObj)
                                {
                                    pObj.ProgressChanged -= OnPythonProgressChanged;
                                }
                                _pythonProgressWindow?.Close();
                            }
                            catch { }
                            _currentPythonProgress = null;
                            _pythonProgressWindow = null;
                            _pythonProgressTextBlock = null;
                        });
                    };
                }

                DisableAvaloniaDataAnnotationValidation();

                desktop.ShutdownRequested += (sender, args) =>
                {
                    Dispose();
                };

                var mainViewModel = Services.GetRequiredService<MainWindowViewModel>();
                
                // Initialize Theme and Chart Settings
                var themeManager = Services.GetRequiredService<IThemeManager>();
                var chartSettingsManager = Services.GetRequiredService<StockAnalyzer.Core.Services.IChartSettingsManager>();
                var fontSettingsManager = Services.GetRequiredService<IFontSettingsManager>();
                
                // Subscribe to theme changes for dynamic resource sync
                themeManager.PropertyChanged += (s, e) =>
                {
                    if (e.PropertyName == nameof(IThemeManager.CurrentTheme))
                    {
                        SyncThemeResources(themeManager.CurrentTheme);
                    }
                };

                _ = themeManager.LoadAsync().ContinueWith(_ => 
                {
                    SyncThemeResources(themeManager.CurrentTheme);
                }, TaskScheduler.FromCurrentSynchronizationContext());

                _ = chartSettingsManager.LoadAsync();
                _ = fontSettingsManager.LoadAsync();

                desktop.MainWindow = new MainWindow
                {
                    DataContext = mainViewModel,
                };
                
                _ = mainViewModel.TryLoadDefaultWorkspaceAsync();
            }

            base.OnFrameworkInitializationCompleted();
        }
        catch (Exception ex)
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.MainWindow = new Window
                {
                    Title = "Startup Error",
                    Width = 800,
                    Height = 600,
                    Content = new ScrollViewer {
                        Content = new TextBlock {
                            Text = $"Error during application startup:\n\n{ex}\n\nStack Trace:\n{ex.StackTrace}",
                            Margin = new Thickness(20),
                            TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
                            FontFamily = new global::Avalonia.Media.FontFamily("Consolas")
                        }
                    }
                };
            }
            else
            {
                throw;
            }
        }
    }

    private void DisableAvaloniaDataAnnotationValidation()
    {
        // Get an array of plugins to remove
        var dataValidationPluginsToRemove =
            BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToArray();

        // remove each entry found
        foreach (var plugin in dataValidationPluginsToRemove)
        {
            BindingPlugins.DataValidators.Remove(plugin);
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            if (Services is IAsyncDisposable asyncDisposable)
            {
                asyncDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            else if (Services is IDisposable disposable)
            {
                disposable.Dispose();
            }
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }

    public void OnRequestedThemeVariantChanged(object? sender, EventArgs e)
    {
        // When in System (Default) mode, ensure the ThemeManager's Skia palette stays in sync with Avalonia's ActualThemeVariant
        var themeManager = Services?.GetService<IThemeManager>() as ThemeManager;
        if (themeManager != null && themeManager.CurrentMode == AppThemeMode.System)
        {
            var actual = GetActualThemeMode();
            bool targetIsDark = (actual == AppThemeMode.Dark);
            if (themeManager.CurrentTheme.IsDark != targetIsDark)
            {
                themeManager.ChangeTheme(targetIsDark ? ThemeColors.Dark : ThemeColors.Light);
            }
        }
    }

    /// <inheritdoc/>
    public void ApplyTheme(AppThemeMode mode)
    {
        var variant = mode switch
        {
            AppThemeMode.Light => global::Avalonia.Styling.ThemeVariant.Light,
            AppThemeMode.Dark => global::Avalonia.Styling.ThemeVariant.Dark,
            _ => global::Avalonia.Styling.ThemeVariant.Default
        };

        if (global::Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            RequestedThemeVariant = variant;
        }
        else
        {
            global::Avalonia.Threading.Dispatcher.UIThread.Post(() => RequestedThemeVariant = variant);
        }
    }

    /// <inheritdoc/>
    public AppThemeMode GetActualThemeMode()
    {
        var actual = ActualThemeVariant;
        return actual == global::Avalonia.Styling.ThemeVariant.Light ? AppThemeMode.Light : AppThemeMode.Dark;
    }

    private void SyncThemeResources(ThemeColors colors)
    {
        if (!global::Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            global::Avalonia.Threading.Dispatcher.UIThread.Post(() => SyncThemeResources(colors));
            return;
        }

        // Helper to convert IndicatorColor to Avalonia Color
        static global::Avalonia.Media.Color ToAvColor(IndicatorColor c) => 
            global::Avalonia.Media.Color.FromArgb(c.A, c.R, c.G, c.B);

        void SetOrUpdateBrush(string key, global::Avalonia.Media.Color color)
        {
            if (Resources.TryGetValue(key, out var obj) && obj is SolidColorBrush brush)
            {
                brush.Color = color;
            }
            else
            {
                Resources[key] = new SolidColorBrush(color);
            }
        }

        // Update Backgrounds
        var bg = ToAvColor(colors.ShellBackground);
        Resources["Color.Background.Primary"] = bg;
        Resources["Color.Background.Secondary"] = bg;
        Resources["Color.Background.Tertiary"] = bg;

        // Update Texts
        var text = ToAvColor(colors.ShellText);
        Resources["Color.Text.Primary"] = text;
        Resources["Color.Foreground.Primary"] = text;

        // Update Accents
        var accent = ToAvColor(colors.ShellAccent);
        Resources["Color.Accent.Primary"] = accent;
        SetOrUpdateBrush("Stock.Brush.Accent", accent);

        // Update Button Colors
        var btnBg = ToAvColor(colors.ButtonBackground);
        var btnText = ToAvColor(colors.ButtonText);
        var btnHover = ToAvColor(colors.ButtonHover);
        var btnPressed = ToAvColor(colors.ButtonPressed);

        Resources["Color_ActionPrimary"] = btnBg;
        Resources["Color_ActionPrimaryHover"] = btnHover;
        SetOrUpdateBrush("Brush_ActionPrimary", btnBg);
        SetOrUpdateBrush("Brush_ActionPrimaryHover", btnHover);
        SetOrUpdateBrush("Brush.Button.Background", btnBg);
        SetOrUpdateBrush("Brush.Button.Text", btnText);
        SetOrUpdateBrush("Brush.Button.Hover", btnHover);
        SetOrUpdateBrush("Brush.Button.Pressed", btnPressed);

        // Set secondary button colors directly to the custom button background color
        Resources["Color_ActionSecondary"] = btnBg;
        Resources["Color_ActionSecondaryHover"] = btnHover;
        SetOrUpdateBrush("Brush_ActionSecondary", btnBg);
        SetOrUpdateBrush("Brush_ActionSecondaryHover", btnHover);

        // Set Stock action button brushes
        SetOrUpdateBrush("Stock.Brush.Background", btnBg);
        SetOrUpdateBrush("Stock.Brush.Background.Hover", btnHover);

        // Update FluentTheme accent button resources (used for OK/Cancel/Apply action buttons)
        SetOrUpdateBrush("AccentButtonBackground", btnBg);
        SetOrUpdateBrush("AccentButtonForeground", btnText);
        SetOrUpdateBrush("AccentButtonBackgroundPointerOver", btnHover);
        SetOrUpdateBrush("AccentButtonBackgroundPressed", btnPressed);

        // Separate DropDownButton and ComboBox controls from custom ButtonBackground using Core ThemeColors
        var ctrlBg = ToAvColor(colors.ControlBackground);
        var ctrlHover = ToAvColor(colors.ControlBackgroundHover);
        var ctrlPressed = ToAvColor(colors.ControlBackgroundPressed);

        // Standard default button resources
        SetOrUpdateBrush("ButtonBackground", ctrlBg);
        SetOrUpdateBrush("ButtonForeground", text);
        SetOrUpdateBrush("ButtonBackgroundPointerOver", ctrlHover);
        SetOrUpdateBrush("ButtonBackgroundPressed", ctrlPressed);

        // DropDownButton resources (matching glyph foreground to text color)
        SetOrUpdateBrush("DropDownButtonBackground", ctrlBg);
        SetOrUpdateBrush("DropDownButtonForeground", text);
        SetOrUpdateBrush("DropDownButtonChevronForeground", text);
        SetOrUpdateBrush("DropDownButtonBackgroundPointerOver", ctrlHover);
        SetOrUpdateBrush("DropDownButtonForegroundPointerOver", text);
        SetOrUpdateBrush("DropDownButtonBackgroundPressed", ctrlPressed);
        SetOrUpdateBrush("DropDownButtonForegroundPressed", text);

        // ComboBox resources (matching glyph foreground to text color)
        SetOrUpdateBrush("ComboBoxBackground", ctrlBg);
        SetOrUpdateBrush("ComboBoxForeground", text);
        SetOrUpdateBrush("ComboBoxDropDownGlyphForeground", text);
        SetOrUpdateBrush("ComboBoxBackgroundPointerOver", ctrlHover);
        SetOrUpdateBrush("ComboBoxForegroundPointerOver", text);
        SetOrUpdateBrush("ComboBoxBackgroundPressed", ctrlPressed);
        SetOrUpdateBrush("ComboBoxForegroundPressed", text);

        // Set button text color
        Resources["Color.Text.Inverse"] = btnText;
        SetOrUpdateBrush("Brush.Text.Inverse", btnText);

        // Update Borders
        var border = ToAvColor(colors.ShellBorder);
        Resources["Color.Border.Primary"] = border;
        SetOrUpdateBrush("Brush.Border", border);

        // Update Semantic Colors
        var semPlus = ToAvColor(colors.SemanticPlus);
        var semMinus = ToAvColor(colors.SemanticMinus);
        var semNeutral = ToAvColor(colors.SemanticNeutral);
        Resources["Color.Semantic.Success"] = semPlus;
        Resources["Color.Semantic.Error"] = semMinus;
        Resources["Color.Semantic.Neutral"] = semNeutral;

        SetOrUpdateBrush("Brush.Semantic.Success", semPlus);
        SetOrUpdateBrush("Brush.Semantic.Error", semMinus);
        SetOrUpdateBrush("Brush.Semantic.Neutral", semNeutral);
    }
}
