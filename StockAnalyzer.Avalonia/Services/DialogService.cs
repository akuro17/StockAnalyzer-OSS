using System;
using System.IO;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Controls.ApplicationLifetimes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Models.Watchlist;
using Avalonia.Platform.Storage;
using System.Linq;
using CommunityToolkit.Mvvm.Messaging;
using StockAnalyzer.Avalonia.Views;
using StockAnalyzer.Avalonia.Views.Dialogs;
using StockAnalyzer.Avalonia.Models;
using Avalonia.Interactivity;
using StockAnalyzer.Avalonia.Common;

namespace StockAnalyzer.Avalonia.Services;

public class DialogService : IDialogService
{


    private class MultiSyncProgressSession : IMultiSyncProgressSession
    {
        private readonly Window _window;
        public MultiSyncProgressViewModel ViewModel { get; }

        public MultiSyncProgressSession(Window window, MultiSyncProgressViewModel vm)
        {
            _window = window;
            ViewModel = vm;
        }

        public void Show(object? owner = null)
        {
            if (owner is Window ownerWindow) _window.Show(ownerWindow);
            else _window.Show();
        }

        public void Close() => _window.Close();
        public void Dispose() => Close();
    }

    private readonly IServiceProvider? _serviceProvider;

    public DialogService()
    {
    }

    public DialogService(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    private double GetBaseFontSize() => _serviceProvider?.GetService<IFontSettingsManager>()?.BaseFontSize ?? 14;
    private double GetTitleFontSize() => _serviceProvider?.GetService<IFontSettingsManager>()?.TitleFontSize ?? 20;
    private double GetDetailFontSize() => _serviceProvider?.GetService<IFontSettingsManager>()?.DetailFontSize ?? 12;
    private double GetHelperFontSize() => _serviceProvider?.GetService<IFontSettingsManager>()?.HelperFontSize ?? 11;

    private static Border CreateStandardHeader(string title, string iconGeometry, double titleFontSize, Window window, IBrush? iconBrush = null)
    {
        var bgTertiaryBrush = Application.Current?.FindResource("Brush.Background.Tertiary") as IBrush ?? Brushes.DarkSlateGray;
        var textPrimaryBrush = Application.Current?.FindResource("Brush.Text.Primary") as IBrush ?? Brushes.White;
        var borderPrimaryBrush = Application.Current?.FindResource("Brush.Border.Primary") as IBrush ?? Brushes.DarkGray;
        var accentBrush = iconBrush ?? (Application.Current?.FindResource("Brush.Accent.Primary") as IBrush ?? Brushes.DodgerBlue);

        var header = new Border
        {
            Height = 56,
            Background = bgTertiaryBrush,
            BorderBrush = borderPrimaryBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(24, 0),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Spacing = 12,
                Children =
                {
                    new PathIcon
                    {
                        Data = StreamGeometry.Parse(iconGeometry),
                        Width = 20,
                        Height = 20,
                        Foreground = accentBrush
                    },
                    new TextBlock
                    {
                        Text = title,
                        FontSize = titleFontSize,
                        FontWeight = FontWeight.SemiBold,
                        Foreground = textPrimaryBrush,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                }
            }
        };

        header.PointerPressed += (s, e) => window.BeginMoveDrag(e);
        return header;
    }

    private static Border CreateStandardFooter(IEnumerable<Control> buttons)
    {
        var bgTertiaryBrush = Application.Current?.FindResource("Brush.Background.Tertiary") as IBrush ?? Brushes.DarkSlateGray;
        var borderPrimaryBrush = Application.Current?.FindResource("Brush.Border.Primary") as IBrush ?? Brushes.DarkGray;

        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 12
        };
        foreach (var btn in buttons)
        {
            panel.Children.Add(btn);
        }

        return new Border
        {
            Height = 64,
            Background = bgTertiaryBrush,
            BorderBrush = borderPrimaryBrush,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(24, 0),
            Child = panel
        };
    }

    public async Task ShowAlertAsync(string title, string message)
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
             await global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
             {
                var bgPrimaryBrush = Application.Current!.FindResource("Brush.Background.Primary") as IBrush ?? Brushes.Black;
                var textPrimaryBrush = Application.Current!.FindResource("Brush.Text.Primary") as IBrush ?? Brushes.White;
                var borderPrimaryBrush = Application.Current!.FindResource("Brush.Border.Primary") as IBrush ?? Brushes.DarkGray;
                var warningBrush = Application.Current!.FindResource("Brush.Semantic.Warning") as IBrush 
                    ?? Application.Current!.FindResource("Brush.Accent.Primary") as IBrush 
                    ?? Brushes.Orange;

                var btnOk = new Button 
                { 
                    Content = LocalizationManager.Instance["Btn_OK"] ?? "OK", 
                    Width = 90,
                    Height = 36,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    IsDefault = true,
                    IsCancel = true,
                    CornerRadius = new CornerRadius(4)
                };
                btnOk.Classes.Add("accent");

                var window = new Window
                {
                    Title = title,
                    MinWidth = 420,
                    MinHeight = 180,
                    SizeToContent = SizeToContent.Height,
                    Width = 460,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    CanResize = false,
                    ExtendClientAreaToDecorationsHint = true,
                    ExtendClientAreaChromeHints = global::Avalonia.Platform.ExtendClientAreaChromeHints.NoChrome,
                    ExtendClientAreaTitleBarHeightHint = -1,
                    Background = bgPrimaryBrush,
                    Foreground = textPrimaryBrush,
                    Icon = desktop.MainWindow?.Icon
                };

                var header = CreateStandardHeader(title, SharedIconGeometries.Warning, GetTitleFontSize(), window, warningBrush);

                var body = new Border
                {
                    Padding = new Thickness(24, 20),
                    Background = bgPrimaryBrush,
                    Child = new TextBlock 
                    { 
                        Text = message, 
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = textPrimaryBrush,
                        FontSize = GetBaseFontSize()
                    }
                };

                var footer = CreateStandardFooter(new[] { btnOk });

                var grid = new Grid
                {
                    RowDefinitions = new RowDefinitions("56, *, 64")
                };
                Grid.SetRow(header, 0);
                Grid.SetRow(body, 1);
                Grid.SetRow(footer, 2);
                grid.Children.Add(header);
                grid.Children.Add(body);
                grid.Children.Add(footer);

                window.Content = new Border
                {
                    BorderBrush = borderPrimaryBrush,
                    BorderThickness = new Thickness(1),
                    Child = grid
                };

                EventHandler<RoutedEventArgs>? okHandler = null;
                okHandler = new EventHandler<RoutedEventArgs>((sender, e) => { btnOk.Click -= okHandler; window.Close(); });
                btnOk.Click += okHandler;

                if (desktop.MainWindow != null)
                {
                    await window.ShowDialog(desktop.MainWindow);
                }
                else
                {
                    window.Show();
                }
             });
        }
        else
        {
            System.Diagnostics.Debug.WriteLine($"[Alert] {title}: {message}");
        }
    }

    public async Task<bool> ShowConfirmationAsync(string title, string message)
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
             return await global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
             {
                var bgPrimaryBrush = Application.Current!.FindResource("Brush.Background.Primary") as IBrush ?? Brushes.Black;
                var textPrimaryBrush = Application.Current!.FindResource("Brush.Text.Primary") as IBrush ?? Brushes.White;
                var borderPrimaryBrush = Application.Current!.FindResource("Brush.Border.Primary") as IBrush ?? Brushes.DarkGray;
                var accentBrush = Application.Current!.FindResource("Brush.Accent.Primary") as IBrush ?? Brushes.DodgerBlue;

                var btnYes = new Button 
                { 
                    Content = LocalizationManager.Instance["Btn_Yes"] ?? "Yes", 
                    Width = 90, 
                    Height = 36,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    IsDefault = true, 
                    CornerRadius = new CornerRadius(4)
                };
                var btnNo = new Button 
                { 
                    Content = LocalizationManager.Instance["Btn_No"] ?? "No", 
                    Width = 90, 
                    Height = 36,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    IsCancel = true, 
                    CornerRadius = new CornerRadius(4)
                };

                btnYes.Classes.Add("accent");
                btnNo.Classes.Add("accent");

                var window = new Window
                {
                    Title = title,
                    MinWidth = 420,
                    MinHeight = 180,
                    SizeToContent = SizeToContent.Height,
                    Width = 460,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    CanResize = false,
                    ExtendClientAreaToDecorationsHint = true,
                    ExtendClientAreaChromeHints = global::Avalonia.Platform.ExtendClientAreaChromeHints.NoChrome,
                    ExtendClientAreaTitleBarHeightHint = -1,
                    Background = bgPrimaryBrush,
                    Foreground = textPrimaryBrush,
                    Icon = desktop.MainWindow?.Icon
                };

                var header = CreateStandardHeader(title, SharedIconGeometries.Help, GetTitleFontSize(), window, accentBrush);

                var body = new Border
                {
                    Padding = new Thickness(24, 20),
                    Background = bgPrimaryBrush,
                    Child = new TextBlock 
                    { 
                        Text = message, 
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = textPrimaryBrush,
                        FontSize = GetBaseFontSize()
                    }
                };

                // 2-button configuration: [Yes] [No] (in that exact left-to-right order, per SA_UI_SUBWINDOW_STANDARD §1.1 & §5.2)
                var footer = CreateStandardFooter(new[] { btnYes, btnNo });

                var grid = new Grid
                {
                    RowDefinitions = new RowDefinitions("56, *, 64")
                };
                Grid.SetRow(header, 0);
                Grid.SetRow(body, 1);
                Grid.SetRow(footer, 2);
                grid.Children.Add(header);
                grid.Children.Add(body);
                grid.Children.Add(footer);

                window.Content = new Border
                {
                    BorderBrush = borderPrimaryBrush,
                    BorderThickness = new Thickness(1),
                    Child = grid
                };

                bool result = false;

                EventHandler<RoutedEventArgs>? yesHandler = null;
                EventHandler<RoutedEventArgs>? noHandler = null;
                yesHandler = new EventHandler<RoutedEventArgs>((sender, e) => { btnYes.Click -= yesHandler; btnNo.Click -= noHandler; result = true; window.Close(); });
                noHandler = new EventHandler<RoutedEventArgs>((sender, e) => { btnYes.Click -= yesHandler; btnNo.Click -= noHandler; result = false; window.Close(); });
                btnYes.Click += yesHandler;
                btnNo.Click += noHandler;

                if (desktop.MainWindow != null)
                {
                    await window.ShowDialog(desktop.MainWindow);
                }
                else
                {
                    window.Show();
                }
                return result;
             });
        }
        
        System.Diagnostics.Debug.WriteLine($"[Confirm] {title}: {message} (Defaulting to false for safety)");
        return false; 
    }

    public async Task<string?> ShowInputAsync(string title, string message, string defaultValue = "")
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
             return await global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
             {
                var bgPrimaryBrush = Application.Current!.FindResource("Brush.Background.Primary") as IBrush ?? Brushes.Black;
                var textPrimaryBrush = Application.Current!.FindResource("Brush.Text.Primary") as IBrush ?? Brushes.White;
                var borderPrimaryBrush = Application.Current!.FindResource("Brush.Border.Primary") as IBrush ?? Brushes.DarkGray;
                var accentBrush = Application.Current!.FindResource("Brush.Accent.Primary") as IBrush ?? Brushes.DodgerBlue;

                var input = new TextBox 
                { 
                    Text = defaultValue, 
                    Height = 36,
                    FontSize = GetBaseFontSize(),
                    Margin = new Thickness(0, 12, 0, 0) 
                };
                var btnOk = new Button 
                { 
                    Content = LocalizationManager.Instance["Btn_OK"] ?? "OK", 
                    Width = 90,
                    Height = 36,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    IsDefault = true, 
                    CornerRadius = new CornerRadius(4) 
                };
                var btnCancel = new Button 
                { 
                    Content = LocalizationManager.Instance["Btn_Cancel"] ?? "Cancel", 
                    Width = 90,
                    Height = 36,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    IsCancel = true, 
                    CornerRadius = new CornerRadius(4) 
                };

                btnOk.Classes.Add("accent");
                btnCancel.Classes.Add("accent");

                var window = new Window
                {
                    Title = title,
                    MinWidth = 420,
                    MinHeight = 220,
                    SizeToContent = SizeToContent.Height,
                    Width = 460,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    CanResize = false,
                    ExtendClientAreaToDecorationsHint = true,
                    ExtendClientAreaChromeHints = global::Avalonia.Platform.ExtendClientAreaChromeHints.NoChrome,
                    ExtendClientAreaTitleBarHeightHint = -1,
                    Background = bgPrimaryBrush,
                    Foreground = textPrimaryBrush,
                    Icon = desktop.MainWindow?.Icon
                };

                var header = CreateStandardHeader(title, SharedIconGeometries.Edit, GetTitleFontSize(), window, accentBrush);

                var body = new Border
                {
                    Padding = new Thickness(24, 20),
                    Background = bgPrimaryBrush,
                    Child = new StackPanel
                    {
                        Children =
                        {
                            new TextBlock 
                            { 
                                Text = message, 
                                TextWrapping = TextWrapping.Wrap,
                                Foreground = textPrimaryBrush,
                                FontSize = GetBaseFontSize()
                            },
                            input
                        }
                    }
                };

                var footer = CreateStandardFooter(new[] { btnOk, btnCancel });

                var grid = new Grid
                {
                    RowDefinitions = new RowDefinitions("56, *, 64")
                };
                Grid.SetRow(header, 0);
                Grid.SetRow(body, 1);
                Grid.SetRow(footer, 2);
                grid.Children.Add(header);
                grid.Children.Add(body);
                grid.Children.Add(footer);

                window.Content = new Border
                {
                    BorderBrush = borderPrimaryBrush,
                    BorderThickness = new Thickness(1),
                    Child = grid
                };
                
                string? result = null;

                btnOk.Click += (_, _) => { result = input.Text; window.Close(); };
                btnCancel.Click += (_, _) => { result = null; window.Close(); };

                if (desktop.MainWindow != null)
                {
                    await window.ShowDialog(desktop.MainWindow);
                }
                else
                {
                    window.Show();
                }
                return result;
             });
        }
        return null;
    }

    public async Task<AddTickerResult> ShowAddTickerDialogAsync(Guid targetProfileId)
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null)
        {
            return await global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
            {
                if (_serviceProvider == null) return new AddTickerResult(null, false, Array.Empty<Guid>(), null);

                var vm = ActivatorUtilities.CreateInstance<AddTickerViewModel>(_serviceProvider, targetProfileId);
                var window = new AddTickerWindow { DataContext = vm };

                var symbol = await window.ShowDialog<string?>(desktop.MainWindow);
                return vm.Result;
            });
        }
        return new AddTickerResult(null, false, Array.Empty<Guid>(), null);
    }

    public async Task<BulkTagEditResult?> ShowBulkTagEditDialogAsync(IEnumerable<string> existingTags)
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null)
        {
            return await global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var vm = new BulkTagEditViewModel(existingTags);
                var window = new BulkTagEditWindow { DataContext = vm };
                var tcs = new System.Threading.Tasks.TaskCompletionSource<BulkTagEditResult?>();

                window.Closed += (sender, e) =>
                {
                    tcs.TrySetResult(window.Tag as BulkTagEditResult);
                };

                window.Show(); // Show modelessly without owner so main window can overlap it
                return await tcs.Task;
            });
        }
        return null;
    }

    public async Task<Transaction?> ShowEditTransactionDialogAsync(EditTransactionDialogViewModel viewModel)
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null)
        {
            return await global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
            {
                try
                {
                    var dialog = new EditTransactionDialog { DataContext = viewModel };
                    return await dialog.ShowDialog<Transaction?>(desktop.MainWindow);
                }
                finally
                {
                    viewModel.Dispose();
                }
            });
        }
        return null;
    }

    public async Task<(string Text, double FontSize)?> ShowTextDialogAsync(string title, string defaultText = "", double defaultFontSize = 12)
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
             return await global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
             {
                var bgPrimaryBrush = Application.Current!.FindResource("Brush.Background.Primary") as IBrush ?? Brushes.Black;
                var textPrimaryBrush = Application.Current!.FindResource("Brush.Text.Primary") as IBrush ?? Brushes.White;
                var borderPrimaryBrush = Application.Current!.FindResource("Brush.Border.Primary") as IBrush ?? Brushes.DarkGray;
                var accentBrush = Application.Current!.FindResource("Brush.Accent.Primary") as IBrush ?? Brushes.DodgerBlue;

                var input = new TextBox 
                { 
                    Text = defaultText, 
                    Margin = new Thickness(0, 5, 0, 12), 
                    AcceptsReturn = true, 
                    Height = 60,
                    FontSize = GetBaseFontSize()
                };
                var fontSizeLabel = new TextBlock 
                { 
                    Text = LocalizationManager.Instance["Dialog_FontSize"] ?? "Font Size:", 
                    VerticalAlignment = VerticalAlignment.Center,
                    FontSize = GetDetailFontSize(),
                    Foreground = textPrimaryBrush
                };
                var fontSizes = new System.Collections.ObjectModel.ObservableCollection<double> { 10, 12, 14, 16, 18, 20, 24, 32, 48, 64, 72, 96 };
                
                if (!fontSizes.Contains(defaultFontSize))
                {
                    fontSizes.Add(defaultFontSize);
                    // The custom font size is appended at the end; sorting is intentionally skipped.
                }

                var fontSizeCombo = new ComboBox 
                { 
                    ItemsSource = fontSizes,
                    SelectedItem = defaultFontSize,
                    Width = 100,
                    Height = 36
                };
                
                var fontPanel = new StackPanel 
                { 
                    Orientation = Orientation.Horizontal,
                    Spacing = 10,
                    Children = { fontSizeLabel, fontSizeCombo }
                };

                var btnOk = new Button 
                { 
                    Content = LocalizationManager.Instance["Btn_OK"] ?? "OK", 
                    Width = 90,
                    Height = 36,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    IsDefault = true, 
                    CornerRadius = new CornerRadius(4),
                    Tag = true 
                };
                var btnCancel = new Button 
                { 
                    Content = LocalizationManager.Instance["Btn_Cancel"] ?? "Cancel", 
                    Width = 90,
                    Height = 36,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    IsCancel = true, 
                    CornerRadius = new CornerRadius(4),
                    Tag = false 
                };

                btnOk.Classes.Add("accent");
                btnCancel.Classes.Add("accent");

                var window = new Window
                {
                    Title = title,
                    MinWidth = 420,
                    MinHeight = 260,
                    SizeToContent = SizeToContent.Height,
                    Width = 480,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    CanResize = false,
                    ExtendClientAreaToDecorationsHint = true,
                    ExtendClientAreaChromeHints = global::Avalonia.Platform.ExtendClientAreaChromeHints.NoChrome,
                    ExtendClientAreaTitleBarHeightHint = -1,
                    Background = bgPrimaryBrush,
                    Foreground = textPrimaryBrush,
                    Icon = desktop.MainWindow?.Icon
                };

                var header = CreateStandardHeader(title, SharedIconGeometries.Edit, GetTitleFontSize(), window, accentBrush);

                var body = new Border
                {
                    Padding = new Thickness(24, 20),
                    Background = bgPrimaryBrush,
                    Child = new StackPanel
                    {
                        Children =
                        {
                            new TextBlock { Text = LocalizationManager.Instance["Dialog_Text"] ?? "Text:", Margin = new Thickness(0,0,0,5), FontSize = GetDetailFontSize(), Foreground = textPrimaryBrush },
                            input,
                            fontPanel
                        }
                    }
                };

                var footer = CreateStandardFooter(new[] { btnOk, btnCancel });

                var grid = new Grid
                {
                    RowDefinitions = new RowDefinitions("56, *, 64")
                };
                Grid.SetRow(header, 0);
                Grid.SetRow(body, 1);
                Grid.SetRow(footer, 2);
                grid.Children.Add(header);
                grid.Children.Add(body);
                grid.Children.Add(footer);

                window.Content = new Border
                {
                    BorderBrush = borderPrimaryBrush,
                    BorderThickness = new Thickness(1),
                    Child = grid
                };
                
                (string Text, double FontSize)? result = null;

                btnOk.Click += (_, _) => 
                { 
                    double fs = (double?)fontSizeCombo.SelectedItem ?? 12.0;
                    result = (input.Text ?? "", fs); 
                    window.Close(); 
                };
                btnCancel.Click += (_, _) => { result = null; window.Close(); };

                if (desktop.MainWindow != null)
                {
                    await window.ShowDialog(desktop.MainWindow);
                }
                else
                {
                    window.Show();
                }
                return result;
             });
        }
        return null;
    }
    public async Task<DrawingSettingsResult> ShowDrawingSettingsDialogAsync(StockAnalyzer.Avalonia.Drawing.IChartObject drawing)
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
             return await global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
             {
                var registry = _serviceProvider?.GetService<IDrawingSettingsPanelRegistry>();
                var dialog = new Views.Dialogs.DrawingSettingsDialog(drawing, registry);
                var result = await dialog.ShowDialog<DrawingSettingsResult?>(desktop.MainWindow!);
                return result ?? DrawingSettingsResult.None;
             });
        }
        return DrawingSettingsResult.None;
    }
    public async Task<Color?> ShowColorPickerAsync(Color initialColor)
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            return await global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var dialog = new Views.Dialogs.ColorPickerDialog(initialColor);
                var result = await dialog.ShowDialog<Color?>(desktop.MainWindow!);
                return result;
            });
        }
        return null;
    }

    public async Task ShowIndicatorSettingsDialogAsync(IEnumerable<CoreIndicatorSettings> currentIndicators, Action<IEnumerable<CoreIndicatorSettings>>? onApply = null)
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            await global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var settingsWindow = new Views.IndicatorSettingsWindow();
                var indicatorFactory = _serviceProvider?.GetService<StockAnalyzer.Core.Models.Indicators.IIndicatorFactory>() 
                    ?? StockAnalyzer.Core.Models.Indicators.IndicatorFactory.Default;
                var toastService = _serviceProvider?.GetService<IToastNotificationService>() 
                ?? throw new InvalidOperationException("IToastNotificationService must be registered in DI.");
                var templateService = _serviceProvider?.GetRequiredService<StockAnalyzer.Core.Interfaces.ITemplateService>() 
                    ?? throw new InvalidOperationException("ITemplateService must be registered in DI.");
                var vm = new IndicatorSettingsDialogViewModel(this, indicatorFactory, toastService, templateService);
                try
                {
                    vm.OnApplyCallback = onApply;
                    vm.Initialize(currentIndicators);
                    settingsWindow.DataContext = vm;
                    await settingsWindow.ShowDialog(desktop.MainWindow!);
                }
                finally
                {
                    vm.Dispose();
                }
            });
        }
    }

    public async Task ShowIndicatorPropertiesDialogAsync(
        CoreIndicatorSettings indicator, 
        Action<CoreIndicatorSettings>? onApply = null, 
        IEnumerable<CoreIndicatorSettings>? allIndicators = null)
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            await global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var dialog = new Views.Dialogs.IndicatorPropertiesDialog();
                var messenger = _serviceProvider?.GetRequiredService<IMessenger>() ?? WeakReferenceMessenger.Default;
                var dispatcher = _serviceProvider?.GetRequiredService<StockAnalyzer.Core.Services.IDispatcherService>() 
                                ?? new StockAnalyzer.Avalonia.Services.DispatcherService();
                var vm = new IndicatorPropertiesViewModel(indicator, messenger, dispatcher, allIndicators);
                vm.OnApplyCallback = onApply;
                dialog.DataContext = vm;
                await dialog.ShowDialog(desktop.MainWindow!); // Use ShowDialog (modal) to prevent race conditions on close
            });
        }
    }

    public async Task ShowThemeSettingsDialogAsync()
    {
        await ShowSettingsDialogAsync(SettingsConstants.Keys.Theme);
    }

    public async Task<List<string>?> ShowColumnChooserDialogAsync(IEnumerable<WatchlistColumnMetadata> allColumns, IEnumerable<string> activeColumns, Action<List<string>>? onApply = null)
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            return await global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var messenger = _serviceProvider?.GetRequiredService<IMessenger>() ?? WeakReferenceMessenger.Default;
                var templateService = _serviceProvider?.GetRequiredService<StockAnalyzer.Core.Interfaces.ITemplateService>()
                    ?? throw new InvalidOperationException("ITemplateService must be registered in DI.");
                var logger = _serviceProvider?.GetService<ILogger<ColumnChooserViewModel>>();
                var vm = new ColumnChooserViewModel(allColumns, activeColumns, messenger, templateService, logger)
                {
                    OnApplyAction = onApply
                };
                try
                {
                    var window = new ColumnChooserWindow
                    {
                        DataContext = vm
                    };
                    
                    var parent = desktop.MainWindow;
                    var result = await window.ShowDialog<bool>(parent!);
                    return result ? vm.GetActiveColumnNames() : null;
                }
                finally
                {
                    vm.Dispose();
                }
            });
        }
        return null;
    }

    public async Task<bool> ShowEditTickerNotesDialogAsync(string ticker, decimal? longVal = null, decimal? exitLong = null, decimal? stopLossLong = null, decimal? shortVal = null, decimal? exitShort = null, decimal? stopLossShort = null, string? reminder = null, Action<decimal?, decimal?, decimal?, decimal?, decimal?, decimal?, string?>? onSave = null)
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            return await global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
            {
                // Resolved directly (not via ActivatorUtilities.CreateInstance): several of the
                // explicit constructor arguments above are nullable decimals/strings that are
                // routinely null (e.g. a ticker with no strategy metadata yet). Boxing a null
                // Nullable<T> loses its runtime type, so ActivatorUtilities' reflection-based
                // parameter matching cannot map it to the correct constructor parameter and
                // throws "A suitable constructor... could not be located", crashing the app.
                var vm = new StockAnalyzer.Avalonia.ViewModels.Dialogs.EditTickerNotesDialogViewModel(
                    ticker, longVal, exitLong, stopLossLong, shortVal, exitShort, stopLossShort, reminder, onSave,
                    _serviceProvider?.GetService<StockAnalyzer.Core.Services.IMarketDataProvider>(),
                    _serviceProvider?.GetService<IDialogService>(),
                    _serviceProvider?.GetService<ITickerSyncService>());
                var window = new StockAnalyzer.Avalonia.Views.Dialogs.EditTickerNotesDialog
                {
                    DataContext = vm
                };
                var parent = desktop.MainWindow;

                // Non-modal: use TaskCompletionSource to await the result without blocking the parent window.
                // This allows the user to switch focus between the Dashboard and MainWindow freely.
                var tcs = new global::System.Threading.Tasks.TaskCompletionSource<bool>();
                vm.CloseAction = r =>
                {
                    tcs.TrySetResult(r); // Set result before closing to win the race with Closed event
                    window.Close();
                };
                // Fallback: if the user closes via the title-bar X button, resolve as false
                window.Closed += (_, _) => tcs.TrySetResult(false);

                window.Show(parent!);
                var result = await tcs.Task;
                if (result && onSave != null)
                {
                    onSave(vm.Long, vm.ExitLong, vm.StopLossLong, vm.Short, vm.ExitShort, vm.StopLossShort, vm.Reminder);
                }
                return result;
            });
        }
        return false;
    }

    [System.Obsolete("Use the 6-parameter overload (longVal, exitLong, stopLossLong, shortVal, exitShort, stopLossShort, reminder) instead.")]
    public async Task<bool> ShowEditTickerNotesDialogAsync(string ticker, decimal? entryPrice, decimal? targetPrice, decimal? stopLoss, string? reminder, Action<decimal?, decimal?, decimal?, string?>? onSave)
    {
        return await ShowEditTickerNotesDialogAsync(ticker, entryPrice, targetPrice, stopLoss, null, null, null, reminder,
            (l, el, sll, s, es, sls, n) => onSave?.Invoke(l, el, sll, n));
    }

    public async Task<StockAnalyzer.Core.Models.Settings.FilterSettings?> ShowFilterSettingsDialogAsync(
        StockAnalyzer.Core.Models.Settings.FilterSettings initialSettings, 
        Action<StockAnalyzer.Core.Models.Settings.FilterSettings>? onApply = null)
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            return await global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var vm = Microsoft.Extensions.DependencyInjection.ActivatorUtilities.CreateInstance<FilterSettingsViewModel>(_serviceProvider, initialSettings);
                if (onApply != null) vm.OnApplyCallback = onApply;
                var window = new Views.Dialogs.FilterSettingsWindow
                {
                    DataContext = vm
                };
                var parent = desktop.MainWindow;
                var result = await window.ShowDialog<StockAnalyzer.Core.Models.Settings.FilterSettings>(parent!);
                return result;
            });
        }
        return null;
    }

    public async Task ShowFilterTemplatePickerDialogAsync(
        StockAnalyzer.Avalonia.ViewModels.TickerListViewModel owner,
        StockAnalyzer.Avalonia.ViewModels.TickerList.FilterNode targetNode)
        => await ShowFilterTemplatePickerDialogInternalAsync(owner, targetNode);

    public async Task ShowFilterTemplatePickerForNewFilterDialogAsync(
        StockAnalyzer.Avalonia.ViewModels.TickerListViewModel owner,
        StockAnalyzer.Avalonia.ViewModels.TickerList.TickerGroupNode parentNode)
        => await ShowFilterTemplatePickerDialogInternalAsync(owner, parentNode);

    private async Task ShowFilterTemplatePickerDialogInternalAsync(
        StockAnalyzer.Avalonia.ViewModels.TickerListViewModel owner,
        StockAnalyzer.Avalonia.ViewModels.TickerList.TickerGroupNode node)
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            await global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var vm = Microsoft.Extensions.DependencyInjection.ActivatorUtilities.CreateInstance<FilterTemplatePickerDialogViewModel>(_serviceProvider, owner, node);
                try
                {
                    var window = new Views.Dialogs.FilterTemplatePickerDialog
                    {
                        DataContext = vm
                    };
                    var parent = desktop.MainWindow;
                    await window.ShowDialog(parent!);
                }
                finally
                {
                    vm.Dispose();
                }
            });
        }
    }

    public async Task ShowSettingsDialogAsync(string? initialCategoryKey = null)
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            await global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
            {
                if (_serviceProvider != null)
                {
                    try 
                    {
                        var vm = _serviceProvider.GetRequiredService<SettingsViewModel>();
                        
                        if (!string.IsNullOrEmpty(initialCategoryKey))
                        {
                            // Flatten categories to find the matching one
                            var allCategories = FlattenCategories(SettingsConstants.Categories);
                            var target = allCategories.FirstOrDefault(c => c.Key == initialCategoryKey);
                            if (target != null)
                            {
                                vm.SelectedCategory = target;
                            }
                        }

                        var window = new SettingsWindow { DataContext = vm };
                        await window.ShowDialog(desktop.MainWindow!);
                    }
                    catch (Exception ex)
                    {
                        await ShowAlertAsync(
                            LocalizationManager.Instance["Dialog_Error"] ?? "Error",
                            $"Failed to initialize Settings: {ex.Message}"
                        );
                    }
                }
            });
        }
    }

    public async Task ShowNoteTrashDialogAsync(StockAnalyzer.Avalonia.ViewModels.Notes.NoteTrashInitialTab initialTab = StockAnalyzer.Avalonia.ViewModels.Notes.NoteTrashInitialTab.Deleted)
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            await global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
            {
                if (_serviceProvider != null)
                {
                    StockAnalyzer.Avalonia.ViewModels.Notes.NoteTrashViewModel? vm = null;
                    try
                    {
                        vm = _serviceProvider.GetRequiredService<StockAnalyzer.Avalonia.ViewModels.Notes.NoteTrashViewModel>();
                        vm.SelectedTabIndex = (int)initialTab;
                        var window = new Views.Notes.NoteTrashWindow { DataContext = vm };
                        await window.ShowDialog(desktop.MainWindow!);
                    }
                    catch (Exception ex)
                    {
                        await ShowAlertAsync(
                            LocalizationManager.Instance["Dialog_Error"] ?? "Error",
                            $"Failed to initialize Trash: {ex.Message}"
                        );
                    }
                    finally
                    {
                        // vm is Transient but INotesSettingsManager (which it subscribes to) is a
                        // Singleton - without disposing here, every dialog open would leak a subscriber.
                        vm?.Dispose();
                    }
                }
            });
        }
    }

    public async Task ShowScreenerDialogAsync()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            await global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                var screenerWindow = new Views.ScreenerWindow();
                if (_serviceProvider != null)
                {
                    try 
                    {
                        var vm = _serviceProvider.GetRequiredService<ScreenerViewModel>();
                        screenerWindow.DataContext = vm;
                        screenerWindow.Show();
                    }
                    catch (Exception ex)
                    {
                        _ = ShowAlertAsync(
                            LocalizationManager.Instance["Dialog_Error"] ?? "Error",
                            $"Failed to initialize Screener: {ex.Message}"
                        );
                    }
                }
            });
        }
    }

    private static IEnumerable<SettingsCategory> FlattenCategories(IEnumerable<SettingsCategory> categories)
        => FlattenInternal(categories);

    private static IEnumerable<SettingsCategory> FlattenInternal(IEnumerable<SettingsCategory> categories)
    {
        foreach (var category in categories)
        {
            yield return category;
            if (category.Children != null)
            {
                foreach (var child in FlattenInternal(category.Children))
                {
                    yield return child;
                }
            }
        }
    }

    public IMultiSyncProgressSession CreateMultiSyncProgressSession()
    {
        if (_serviceProvider == null) throw new InvalidOperationException("ServiceProvider not initialized in DialogService.");
        var vm = _serviceProvider.GetRequiredService<MultiSyncProgressViewModel>();
        var window = new MultiSyncProgressWindow();
        window.DataContext = vm;
        return new MultiSyncProgressSession(window, vm);
    }



    public object? GetMainWindowOwner()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            return desktop.MainWindow;
        }
        return null;
    }

    public async Task ShowLogViewerAsync()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            await global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var logViewerWindow = new Views.LogViewerView();
                if (_serviceProvider != null)
                {
                    try 
                    {
                        var vm = _serviceProvider.GetRequiredService<LogViewerViewModel>();
                        logViewerWindow.DataContext = vm;
                        await logViewerWindow.ShowDialog(desktop.MainWindow!);
                    }
                    catch (Exception ex)
                    {
                        await ShowAlertAsync(
                            LocalizationManager.Instance["Dialog_Error"] ?? "Error",
                            $"Failed to initialize Log Viewer: {ex.Message}"
                        );
                    }
                }
            });
        }
    }

    private static TopLevel? GetActiveTopLevel(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var activeWindow = desktop.Windows.LastOrDefault(w => w.IsActive && w.IsVisible)
            ?? desktop.Windows.LastOrDefault(w => w.IsVisible)
            ?? desktop.MainWindow;

        return activeWindow != null ? TopLevel.GetTopLevel(activeWindow) : null;
    }

    public async Task<string?> ShowOpenFileDialogAsync(string title, string[]? filters = null)
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null)
        {
            return await global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync<string?>(async () =>
            {
                try
                {
                    var topLevel = GetActiveTopLevel(desktop);
                    if (topLevel == null) return null;

                    var options = new FilePickerOpenOptions
                    {
                        Title = title,
                        AllowMultiple = false
                    };

                    if (filters != null && filters.Length > 0)
                    {
                        var fileType = new FilePickerFileType("Files")
                        {
                            Patterns = filters.Select(f => $"*.{f.TrimStart('.')}").ToArray()
                        };
                        options.FileTypeFilter = new[] { fileType };
                    }

                    var files = await topLevel.StorageProvider.OpenFilePickerAsync(options);
                    if (files != null && files.Count > 0)
                    {
                        return files[0].TryGetLocalPath() ?? files[0].Path.LocalPath;
                    }
                }
                catch
                {
                    // Prevent unhandled COM/platform dialog exceptions from crashing the app
                }
                return null;
            });
        }
        return null;
    }

    public async Task<string?> ShowSaveFileDialogAsync(string title, string defaultExtension = "", string defaultFilename = "", string[]? filters = null, string? initialDirectory = null)
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null)
        {
            return await global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync<string?>(async () =>
            {
                try
                {
                    var topLevel = GetActiveTopLevel(desktop);
                    if (topLevel == null) return null;

                    var options = new FilePickerSaveOptions
                    {
                        Title = title,
                        DefaultExtension = defaultExtension.TrimStart('.'),
                        SuggestedFileName = defaultFilename
                    };

                    if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
                    {
                        try
                        {
                            options.SuggestedStartLocation = await topLevel.StorageProvider.TryGetFolderFromPathAsync(initialDirectory!);
                        }
                        catch
                        {
                            // Ignore folder lookup failures
                        }
                    }

                    if (filters != null && filters.Length > 0)
                    {
                        var fileType = new FilePickerFileType("Files")
                        {
                            Patterns = filters.Select(f => $"*.{f.TrimStart('.')}").ToArray()
                        };
                        options.FileTypeChoices = new[] { fileType };
                    }

                    var file = await topLevel.StorageProvider.SaveFilePickerAsync(options);
                    return file?.TryGetLocalPath() ?? file?.Path.LocalPath;
                }
                catch
                {
                    // Prevent unhandled COM/platform dialog exceptions from crashing the app
                }
                return null;
            });
        }
        return null;
    }

    public async Task<string?> ShowOpenFolderDialogAsync(string title, string? initialDirectory = null)
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null)
        {
            return await global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync<string?>(async () =>
            {
                try
                {
                    var topLevel = GetActiveTopLevel(desktop);
                    if (topLevel == null) return null;

                    var options = new FolderPickerOpenOptions
                    {
                        Title = title,
                        AllowMultiple = false
                    };

                    if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
                    {
                        try
                        {
                            options.SuggestedStartLocation = await topLevel.StorageProvider.TryGetFolderFromPathAsync(initialDirectory!);
                        }
                        catch
                        {
                            // Ignore folder lookup failures
                        }
                    }

                    var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(options);
                    if (folders != null && folders.Count > 0)
                    {
                        return folders[0].TryGetLocalPath() ?? folders[0].Path.LocalPath;
                    }
                }
                catch
                {
                    // Prevent unhandled COM/platform dialog exceptions from crashing the app
                }
                return null;
            });
        }
        return null;
    }

    public async Task<bool> ShowExportChartImageDialogAsync(ChartViewModel chartViewModel)
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null)
        {
            return await global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var exportService = _serviceProvider?.GetService<Services.Export.IChartImageExportService>() 
                    ?? new Services.Export.ChartImageExportService();
                var logger = _serviceProvider?.GetService<ILogger<ExportChartImageDialogViewModel>>();
                var marketDataProvider = _serviceProvider?.GetService<StockAnalyzer.Core.Services.IMarketDataProvider>();
                var themeManager = _serviceProvider?.GetService<StockAnalyzer.Core.Theme.IThemeManager>() 
                    ?? new StockAnalyzer.Core.Theme.ThemeManager();

                var bounds = new Rect(0, 0, 1280, 720);
                var layout = Views.Chart.ChartLayoutService.CreateLayout(
                    bounds, 
                    chartViewModel.ChartType, 
                    chartViewModel.Indicators,
                    chartViewModel.EffectiveIsSubWindowVisible,
                    chartViewModel.IsMainWindowVisible,
                    chartViewModel.ChartMarginTop,
                    chartViewModel.ChartMarginBottom,
                    chartViewModel.ChartMarginRight);

                var snapshot = chartViewModel.CurrentSnapshot;
                if (snapshot == null || snapshot.Candles.Count == 0)
                {
                    var candles = chartViewModel.Candles;
                    int visibleCount = chartViewModel.VisibleCandleCount > 0 ? chartViewModel.VisibleCandleCount : Math.Min(candles.Count, 120);
                    int startIndex = Math.Max(0, candles.Count - visibleCount);
                    var visibleCandles = candles.Skip(startIndex).Take(visibleCount).ToList();
                    snapshot = new Views.Chart.ChartDataSnapshot(
                        candles: visibleCandles,
                        symbol: chartViewModel.Symbol,
                        timeframe: chartViewModel.SelectedTimeFrame.ToString(),
                        indicatorResults: chartViewModel.IndicatorResults,
                        indicatorSettings: chartViewModel.Indicators,
                        drawings: null,
                        startIndex: startIndex,
                        count: visibleCount,
                        allPnfCandles: candles,
                        chartType: chartViewModel.ChartType,
                        visibleCandleCount: visibleCount,
                        priceScale: chartViewModel.PriceScale);
                }

                var transform = new StockAnalyzer.Avalonia.Drawing.GenericCoordinateTransform(
                    StockAnalyzer.Avalonia.Drawing.ChartAxisMode.GaplessTime, bounds.Width, bounds.Height);
                transform.PriceScale = chartViewModel.PriceScale;
                transform.UpdateCanvasSize(layout.TotalBounds.Width, layout.TotalBounds.Height, layout.ChartArea.X, layout.ChartArea.Y, layout.ChartArea.Width, layout.ChartArea.Height);
                transform.Metadata = new StockAnalyzer.Avalonia.Drawing.TransformMetadata(chartViewModel.EffectiveIsSubWindowVisible, chartViewModel.IsMainWindowVisible, chartViewModel.ChartType);

                if (chartViewModel.ChartType == StockAnalyzer.Core.Models.ChartType.ReverseWatch)
                {
                    transform.SetMode(StockAnalyzer.Avalonia.Drawing.ChartAxisMode.Volume);
                    transform.SetPriceRange(snapshot.MinPrice, snapshot.MaxPrice);
                }
                else if (chartViewModel.ChartType.IsIndexBased())
                {
                    transform.SetMode(StockAnalyzer.Avalonia.Drawing.ChartAxisMode.Index);
                    var timeMap = snapshot.AllCandles?.Select(c => c.Timestamp).ToList() ?? snapshot.Candles.Select(c => c.Timestamp).ToList();
                    transform.SetTimeMap(timeMap);
                    transform.SetIndexRange(snapshot.StartIndex - 0.5, snapshot.StartIndex + snapshot.VisibleCandleCount + 0.5);
                    transform.SetPriceRange(snapshot.MinPrice, snapshot.MaxPrice);
                }
                else
                {
                    transform.SetMode(StockAnalyzer.Avalonia.Drawing.ChartAxisMode.GaplessTime);
                    var allCandles = snapshot.AllCandles ?? chartViewModel.Candles;
                    var timeMap = allCandles.Select(c => c.Timestamp).ToList();
                    transform.SetTimeMap(timeMap);
                    if (snapshot.Candles.Count > 0)
                    {
                        transform.SetTimeRange(snapshot.Candles[0].Timestamp, snapshot.Candles[^1].Timestamp);
                    }
                    transform.SetPriceRange(snapshot.MinPrice, snapshot.MaxPrice);
                }

                var profile = Views.Chart.ChartTypeProfileRegistry.Get(chartViewModel.ChartType);
                var renderer = profile.CreateRenderer();
                var config = profile.CreateRenderConfig(
                    chartViewModel, 
                    chartViewModel.CurrentPrice, 
                    snapshot.StartIndex, 
                    snapshot.VisibleCandleCount, 
                    new StockAnalyzer.Core.Models.Point(0, 0), 
                    transform, 
                    1.0);

                string companyName = string.Empty;
                if (marketDataProvider != null && !string.IsNullOrWhiteSpace(chartViewModel.Symbol))
                {
                    try
                    {
                        var metadata = await marketDataProvider.GetMetadataAsync(chartViewModel.Symbol);
                        companyName = metadata.ShortName ?? metadata.LongName ?? string.Empty;
                    }
                    catch
                    {
                        // Ignore metadata fetch errors
                    }
                }

                var templateService = _serviceProvider?.GetService<StockAnalyzer.Core.Interfaces.ITemplateService>();

                var vm = new ExportChartImageDialogViewModel(
                    snapshot,
                    layout,
                    transform,
                    chartViewModel.ObjectManager,
                    renderer,
                    config,
                    chartViewModel.ChartType,
                    new Views.Chart.Renderers.RulerRenderer(),
                    themeManager,
                    exportService,
                    this,
                    templateService,
                    chartViewModel.Symbol,
                    companyName,
                    chartViewModel.SelectedTimeFrame.ToString(),
                    logger);

                using (vm)
                {
                    var window = new Views.Dialogs.ExportChartImageView
                    {
                        DataContext = vm
                    };

                    var result = await window.ShowDialog<bool>(desktop.MainWindow);
                    return result;
                }
            });
        }
        return false;
    }

    public async Task<StockAnalyzer.Core.Services.PythonSetupDecision> ShowPythonSetupConfirmationAsync()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
             return await global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
             {
                var bgPrimaryBrush = Application.Current!.FindResource("Brush.Background.Primary") as IBrush ?? Brushes.Black;
                var textPrimaryBrush = Application.Current!.FindResource("Brush.Text.Primary") as IBrush ?? Brushes.White;
                var borderPrimaryBrush = Application.Current!.FindResource("Brush.Border.Primary") as IBrush ?? Brushes.DarkGray;
                var accentBrush = Application.Current!.FindResource("Brush.Accent.Primary") as IBrush ?? Brushes.DodgerBlue;

                var btnAuto = new Button 
                { 
                    Content = LocalizationManager.Instance["Btn_AutoSetup"] ?? "Automatic Setup (Recommended)", 
                    Width = 200, 
                    Height = 36,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    IsDefault = true, 
                    CornerRadius = new CornerRadius(4)
                };
                var btnManual = new Button 
                { 
                    Content = LocalizationManager.Instance["Btn_ConfigureManually"] ?? "Configure Manually", 
                    Width = 140, 
                    Height = 36,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    CornerRadius = new CornerRadius(4)
                };
                var btnCancel = new Button 
                { 
                    Content = LocalizationManager.Instance["Btn_Cancel"] ?? "Cancel", 
                    Width = 100, 
                    Height = 36,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    IsCancel = true, 
                    CornerRadius = new CornerRadius(4)
                };

                btnAuto.Classes.Add("accent");
                btnManual.Classes.Add("accent");
                btnCancel.Classes.Add("accent");

                var title = LocalizationManager.Instance["PythonSetup_Title"] ?? "Python Setup";
                var message = LocalizationManager.Instance["PythonSetup_Message"] ?? "Python environment is not configured.";

                var window = new Window
                {
                    Title = title,
                    MinWidth = 500,
                    MinHeight = 220,
                    SizeToContent = SizeToContent.Height,
                    Width = 520,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    CanResize = false,
                    ExtendClientAreaToDecorationsHint = true,
                    ExtendClientAreaChromeHints = global::Avalonia.Platform.ExtendClientAreaChromeHints.NoChrome,
                    ExtendClientAreaTitleBarHeightHint = -1,
                    Background = bgPrimaryBrush,
                    Foreground = textPrimaryBrush,
                    Icon = desktop.MainWindow?.Icon
                };

                var header = CreateStandardHeader(title, SharedIconGeometries.Settings, GetTitleFontSize(), window, accentBrush);

                var body = new Border
                {
                    Padding = new Thickness(24, 20),
                    Background = bgPrimaryBrush,
                    Child = new TextBlock 
                    { 
                        Text = message, 
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = textPrimaryBrush,
                        FontSize = GetBaseFontSize(),
                        MaxWidth = 470
                    }
                };

                var footer = CreateStandardFooter(new[] { btnAuto, btnManual, btnCancel });

                var grid = new Grid
                {
                    RowDefinitions = new RowDefinitions("56, *, 64")
                };
                Grid.SetRow(header, 0);
                Grid.SetRow(body, 1);
                Grid.SetRow(footer, 2);
                grid.Children.Add(header);
                grid.Children.Add(body);
                grid.Children.Add(footer);

                window.Content = new Border
                {
                    BorderBrush = borderPrimaryBrush,
                    BorderThickness = new Thickness(1),
                    Child = grid
                };

                var result = StockAnalyzer.Core.Services.PythonSetupDecision.Cancel;

                EventHandler<RoutedEventArgs>? autoHandler = null;
                EventHandler<RoutedEventArgs>? manualHandler = null;
                EventHandler<RoutedEventArgs>? cancelHandler = null;

                Action cleanHandlers = () =>
                {
                    btnAuto.Click -= autoHandler;
                    btnManual.Click -= manualHandler;
                    btnCancel.Click -= cancelHandler;
                };

                autoHandler = new EventHandler<RoutedEventArgs>((sender, e) => { cleanHandlers(); result = StockAnalyzer.Core.Services.PythonSetupDecision.Automatic; window.Close(); });
                manualHandler = new EventHandler<RoutedEventArgs>((sender, e) => { cleanHandlers(); result = StockAnalyzer.Core.Services.PythonSetupDecision.Manual; window.Close(); });
                cancelHandler = new EventHandler<RoutedEventArgs>((sender, e) => { cleanHandlers(); result = StockAnalyzer.Core.Services.PythonSetupDecision.Cancel; window.Close(); });

                btnAuto.Click += autoHandler;
                btnManual.Click += manualHandler;
                btnCancel.Click += cancelHandler;

                if (desktop.MainWindow != null)
                {
                    await window.ShowDialog(desktop.MainWindow);
                }
                else
                {
                    window.Show();
                }
                return result;
             });
        }
        return StockAnalyzer.Core.Services.PythonSetupDecision.Cancel;
    }

    public async Task ShowManualSetupInstructionsAsync()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
             await global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
             {
                var bgPrimaryBrush = Application.Current!.FindResource("Brush.Background.Primary") as IBrush ?? Brushes.Black;
                var textPrimaryBrush = Application.Current!.FindResource("Brush.Text.Primary") as IBrush ?? Brushes.White;
                var borderPrimaryBrush = Application.Current!.FindResource("Brush.Border.Primary") as IBrush ?? Brushes.DarkGray;
                var accentBrush = Application.Current!.FindResource("Brush.Accent.Primary") as IBrush ?? Brushes.DodgerBlue;

                var btnOk = new Button 
                { 
                    Content = LocalizationManager.Instance["Btn_OK"] ?? "OK", 
                    Width = 90, 
                    Height = 36,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    IsDefault = true, 
                    CornerRadius = new CornerRadius(4)
                };
                btnOk.Classes.Add("accent");

                var title = LocalizationManager.Instance["PythonSetup_Manual_Title"] ?? "Manual Setup Instructions";

                var window = new Window
                {
                    Title = title,
                    MinWidth = 500,
                    MinHeight = 280,
                    SizeToContent = SizeToContent.Height,
                    Width = 520,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    CanResize = false,
                    ExtendClientAreaToDecorationsHint = true,
                    ExtendClientAreaChromeHints = global::Avalonia.Platform.ExtendClientAreaChromeHints.NoChrome,
                    ExtendClientAreaTitleBarHeightHint = -1,
                    Background = bgPrimaryBrush,
                    Foreground = textPrimaryBrush,
                    Icon = desktop.MainWindow?.Icon
                };

                var header = CreateStandardHeader(title, SharedIconGeometries.Info, GetTitleFontSize(), window, accentBrush);

                var body = new Border
                {
                    Padding = new Thickness(24, 20),
                    Background = bgPrimaryBrush,
                    Child = new TextBlock 
                    { 
                        Text = LocalizationManager.Instance["PythonSetup_Manual_Instructions"] ?? "", 
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = textPrimaryBrush,
                        FontSize = GetBaseFontSize(),
                        MaxWidth = 480
                    }
                };

                var footer = CreateStandardFooter(new[] { btnOk });

                var grid = new Grid
                {
                    RowDefinitions = new RowDefinitions("56, *, 64")
                };
                Grid.SetRow(header, 0);
                Grid.SetRow(body, 1);
                Grid.SetRow(footer, 2);
                grid.Children.Add(header);
                grid.Children.Add(body);
                grid.Children.Add(footer);

                window.Content = new Border
                {
                    BorderBrush = borderPrimaryBrush,
                    BorderThickness = new Thickness(1),
                    Child = grid
                };

                EventHandler<RoutedEventArgs>? okSetupHandler = null;
                okSetupHandler = new EventHandler<RoutedEventArgs>((sender, e) => { btnOk.Click -= okSetupHandler; window.Close(); });
                btnOk.Click += okSetupHandler;

                if (desktop.MainWindow != null)
                {
                    await window.ShowDialog(desktop.MainWindow);
                }
                else
                {
                    window.Show();
                }
             });
        }
    }

    public async Task<StockAnalyzer.Core.Services.PythonSetupDecision> ShowPythonUpdateConfirmationAsync()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
             return await global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
             {
                var bgPrimaryBrush = Application.Current!.FindResource("Brush.Background.Primary") as IBrush ?? Brushes.Black;
                var textPrimaryBrush = Application.Current!.FindResource("Brush.Text.Primary") as IBrush ?? Brushes.White;
                var borderPrimaryBrush = Application.Current!.FindResource("Brush.Border.Primary") as IBrush ?? Brushes.DarkGray;
                var accentBrush = Application.Current!.FindResource("Brush.Accent.Primary") as IBrush ?? Brushes.DodgerBlue;

                var btnAuto = new Button 
                { 
                    Content = LocalizationManager.Instance["Btn_AutoUpdate"] ?? "Update Automatically", 
                    Width = 200, 
                    Height = 36,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    IsDefault = true, 
                    CornerRadius = new CornerRadius(4)
                };
                var btnManual = new Button 
                { 
                    Content = LocalizationManager.Instance["Btn_ConfigureManually"] ?? "Configure Manually", 
                    Width = 140, 
                    Height = 36,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    CornerRadius = new CornerRadius(4)
                };
                var btnCancel = new Button 
                { 
                    Content = LocalizationManager.Instance["Btn_Cancel"] ?? "Cancel", 
                    Width = 100, 
                    Height = 36,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    IsCancel = true, 
                    CornerRadius = new CornerRadius(4)
                };

                btnAuto.Classes.Add("accent");
                btnManual.Classes.Add("accent");
                btnCancel.Classes.Add("accent");

                var title = LocalizationManager.Instance["PythonUpdate_Title"] ?? "Python Library Update";
                var message = LocalizationManager.Instance["PythonUpdate_Message"] ?? "Python environment is configured. Would you like to check for and install updates for the required Python libraries?";

                var window = new Window
                {
                    Title = title,
                    MinWidth = 500,
                    MinHeight = 220,
                    SizeToContent = SizeToContent.Height,
                    Width = 520,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    CanResize = false,
                    ExtendClientAreaToDecorationsHint = true,
                    ExtendClientAreaChromeHints = global::Avalonia.Platform.ExtendClientAreaChromeHints.NoChrome,
                    ExtendClientAreaTitleBarHeightHint = -1,
                    Background = bgPrimaryBrush,
                    Foreground = textPrimaryBrush,
                    Icon = desktop.MainWindow?.Icon
                };

                var header = CreateStandardHeader(title, SharedIconGeometries.Settings, GetTitleFontSize(), window, accentBrush);

                var body = new Border
                {
                    Padding = new Thickness(24, 20),
                    Background = bgPrimaryBrush,
                    Child = new TextBlock 
                    { 
                        Text = message, 
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = textPrimaryBrush,
                        FontSize = GetBaseFontSize(),
                        MaxWidth = 470
                    }
                };

                var footer = CreateStandardFooter(new[] { btnAuto, btnManual, btnCancel });

                var grid = new Grid
                {
                    RowDefinitions = new RowDefinitions("56, *, 64")
                };
                Grid.SetRow(header, 0);
                Grid.SetRow(body, 1);
                Grid.SetRow(footer, 2);
                grid.Children.Add(header);
                grid.Children.Add(body);
                grid.Children.Add(footer);

                window.Content = new Border
                {
                    BorderBrush = borderPrimaryBrush,
                    BorderThickness = new Thickness(1),
                    Child = grid
                };

                var result = StockAnalyzer.Core.Services.PythonSetupDecision.Cancel;

                btnAuto.Click += (_, _) => { result = StockAnalyzer.Core.Services.PythonSetupDecision.Automatic; window.Close(); };
                btnManual.Click += (_, _) => { result = StockAnalyzer.Core.Services.PythonSetupDecision.Manual; window.Close(); };
                btnCancel.Click += (_, _) => { result = StockAnalyzer.Core.Services.PythonSetupDecision.Cancel; window.Close(); };

                if (desktop.MainWindow != null)
                {
                    await window.ShowDialog(desktop.MainWindow);
                }
                else
                {
                    window.Show();
                }
                return result;
             });
        }
        return StockAnalyzer.Core.Services.PythonSetupDecision.Cancel;
    }

    public async Task ShowPythonManualUpdateInstructionsAsync()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
             await global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
             {
                var bgPrimaryBrush = Application.Current!.FindResource("Brush.Background.Primary") as IBrush ?? Brushes.Black;
                var textPrimaryBrush = Application.Current!.FindResource("Brush.Text.Primary") as IBrush ?? Brushes.White;
                var borderPrimaryBrush = Application.Current!.FindResource("Brush.Border.Primary") as IBrush ?? Brushes.DarkGray;
                var accentBrush = Application.Current!.FindResource("Brush.Accent.Primary") as IBrush ?? Brushes.DodgerBlue;

                var btnOk = new Button 
                { 
                    Content = LocalizationManager.Instance["Btn_OK"] ?? "OK", 
                    Width = 90, 
                    Height = 36,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    IsDefault = true, 
                    CornerRadius = new CornerRadius(4)
                };
                btnOk.Classes.Add("accent");

                var title = LocalizationManager.Instance["PythonUpdate_Manual_Title"] ?? "Manual Update Instructions";

                var window = new Window
                {
                    Title = title,
                    MinWidth = 500,
                    MinHeight = 280,
                    SizeToContent = SizeToContent.Height,
                    Width = 520,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    CanResize = false,
                    ExtendClientAreaToDecorationsHint = true,
                    ExtendClientAreaChromeHints = global::Avalonia.Platform.ExtendClientAreaChromeHints.NoChrome,
                    ExtendClientAreaTitleBarHeightHint = -1,
                    Background = bgPrimaryBrush,
                    Foreground = textPrimaryBrush,
                    Icon = desktop.MainWindow?.Icon
                };

                var header = CreateStandardHeader(title, SharedIconGeometries.Info, GetTitleFontSize(), window, accentBrush);

                var body = new Border
                {
                    Padding = new Thickness(24, 20),
                    Background = bgPrimaryBrush,
                    Child = new TextBlock 
                    { 
                        Text = LocalizationManager.Instance["PythonUpdate_Manual_Instructions"] ?? "", 
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = textPrimaryBrush,
                        FontSize = GetBaseFontSize(),
                        MaxWidth = 480
                    }
                };

                var footer = CreateStandardFooter(new[] { btnOk });

                var grid = new Grid
                {
                    RowDefinitions = new RowDefinitions("56, *, 64")
                };
                Grid.SetRow(header, 0);
                Grid.SetRow(body, 1);
                Grid.SetRow(footer, 2);
                grid.Children.Add(header);
                grid.Children.Add(body);
                grid.Children.Add(footer);

                window.Content = new Border
                {
                    BorderBrush = borderPrimaryBrush,
                    BorderThickness = new Thickness(1),
                    Child = grid
                };

                EventHandler<RoutedEventArgs>? okUpdateHandler = null;
                okUpdateHandler = new EventHandler<RoutedEventArgs>((sender, e) => { btnOk.Click -= okUpdateHandler; window.Close(); });
                btnOk.Click += okUpdateHandler;

                if (desktop.MainWindow != null)
                {
                    await window.ShowDialog(desktop.MainWindow);
                }
                else
                {
                    window.Show();
                }
             });
        }
    }

    public async Task RunWithProgressAsync(string title, System.Func<System.IProgress<string>, Task> action)
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            await global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var bgSecondaryBrush = Application.Current!.FindResource("Brush.Background.Secondary") as IBrush ?? Brushes.Black;
                var textPrimaryBrush = Application.Current!.FindResource("Brush.Text.Primary") as IBrush ?? Brushes.White;
                var borderPrimaryBrush = Application.Current!.FindResource("Brush.Border.Primary") as IBrush ?? Brushes.DarkGray;

                var textBlock = new TextBlock 
                { 
                    Text = "Initializing...", 
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = textPrimaryBrush,
                    FontSize = GetBaseFontSize(),
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

                var window = new Window
                {
                    Title = title,
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
                            Children = { textBlock, progressBar }
                        }
                    }
                };

                var progress = new Progress<string>(status =>
                {
                    textBlock.Text = status;
                });

                if (desktop.MainWindow != null)
                {
                    window.Show(desktop.MainWindow);
                }
                else
                {
                    window.Show();
                }

                try
                {
                    await Task.Run(() => action(progress));
                }
                finally
                {
                    window.Close();
                }
            });
        }
    }

    public void Shutdown()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    public void ActivateMainWindow()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null)
        {
            desktop.MainWindow.Activate();
        }
    }
}

