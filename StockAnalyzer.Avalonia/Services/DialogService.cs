using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Controls.ApplicationLifetimes;
using Microsoft.Extensions.DependencyInjection;
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

    public async Task ShowAlertAsync(string title, string message)
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
             await global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
             {
                var btnOk = new Button 
                { 
                    Content = LocalizationManager.Instance["Btn_OK"], 
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Width = 80
                };

                var window = new Window
                {
                    Title = title,
                    MinWidth = 400,
                    MinHeight = 150,
                    SizeToContent = SizeToContent.WidthAndHeight,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    CanResize = false,
                    Content = new StackPanel
                    {
                        Margin = new Thickness(20),
                        Spacing = 20,
                        Children =
                        {
                            new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                            btnOk
                        }
                    }
                };
                EventHandler<RoutedEventArgs>? okHandler = null;
                okHandler = new EventHandler<RoutedEventArgs>((sender, e) => { btnOk.Click -= okHandler; window.Close(); });
                btnOk.Click += okHandler;

                await window.ShowDialog(desktop.MainWindow!);
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
                var successBrush = Application.Current!.FindResource("Brush.Semantic.Success") as IBrush ?? Brushes.Green;
                var textSecondaryBrush = Application.Current!.FindResource("Brush.Text.Secondary") as IBrush ?? Brushes.Gray;
                var bgSecondaryBrush = Application.Current!.FindResource("Brush.Background.Secondary") as IBrush ?? Brushes.Black;
                var textPrimaryBrush = Application.Current!.FindResource("Brush.Text.Primary") as IBrush ?? Brushes.White;
                var borderPrimaryBrush = Application.Current!.FindResource("Brush.Border.Primary") as IBrush ?? Brushes.DarkGray;

                var btnYes = new Button 
                { 
                    Content = LocalizationManager.Instance["Btn_Yes"], 
                    Width = 90, 
                    Height = 36,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    IsDefault = true, 
                    CornerRadius = new CornerRadius(4)
                };
                var btnNo = new Button 
                { 
                    Content = LocalizationManager.Instance["Btn_No"], 
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
                    MinHeight = 160,
                    SizeToContent = SizeToContent.WidthAndHeight,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    CanResize = false,
                    Background = bgSecondaryBrush,
                    Foreground = textPrimaryBrush,
                    Content = new Border
                    {
                        BorderBrush = borderPrimaryBrush,
                        BorderThickness = new Thickness(1),
                        Child = new StackPanel
                        {
                            Margin = new Thickness(24),
                            Spacing = 24,
                            Children =
                            {
                                new TextBlock 
                                { 
                                    Text = message, 
                                    TextWrapping = TextWrapping.Wrap,
                                    Foreground = textPrimaryBrush,
                                    FontSize = GetBaseFontSize()
                                },
                                new StackPanel
                                {
                                    Orientation = Orientation.Horizontal,
                                    HorizontalAlignment = HorizontalAlignment.Right,
                                    Spacing = 12,
                                    Children = { btnYes, btnNo }
                                }
                            }
                        }
                    }
                };

                bool result = false;

                EventHandler<RoutedEventArgs>? yesHandler = null;
                EventHandler<RoutedEventArgs>? noHandler = null;
                yesHandler = new EventHandler<RoutedEventArgs>((sender, e) => { btnYes.Click -= yesHandler; btnNo.Click -= noHandler; result = true; window.Close(); });
                noHandler = new EventHandler<RoutedEventArgs>((sender, e) => { btnYes.Click -= yesHandler; btnNo.Click -= noHandler; result = false; window.Close(); });
                btnYes.Click += yesHandler;
                btnNo.Click += noHandler;

                await window.ShowDialog(desktop.MainWindow!);
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
                var input = new TextBox { Text = defaultValue, Margin = new Thickness(0, 10, 0, 0) };
                var btnOk = new Button { Content = LocalizationManager.Instance["Btn_OK"], IsDefault = true, Width = 80 };
                var btnCancel = new Button { Content = LocalizationManager.Instance["Btn_Cancel"], IsCancel = true, Width = 80 };

                var window = new Window
                {
                    Title = title,
                    MinWidth = 400,
                    MinHeight = 150,
                    SizeToContent = SizeToContent.WidthAndHeight,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    CanResize = false,
                    Content = new StackPanel
                    {
                        Margin = new Thickness(20),
                        Children =
                        {
                            new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                            input,
                            new StackPanel 
                            { 
                                Orientation = Orientation.Horizontal, 
                                HorizontalAlignment = HorizontalAlignment.Right, 
                                Margin = new Thickness(0, 20, 0, 0),
                                Spacing = 10,
                                Children = { btnOk, btnCancel }
                            }
                        }
                    }
                };
                
                string? result = null;

                btnOk.Click += (_, _) => { result = input.Text; window.Close(); };
                btnCancel.Click += (_, _) => { result = null; window.Close(); };

                await window.ShowDialog(desktop.MainWindow!);
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
                var input = new TextBox { Text = defaultText, Margin = new Thickness(0, 5, 0, 10), AcceptsReturn = true, Height = 60 };
                var fontSizeLabel = new TextBlock { Text = LocalizationManager.Instance["Dialog_FontSize"], VerticalAlignment = VerticalAlignment.Center };
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
                    Width = 100
                };
                
                var fontPanel = new StackPanel 
                { 
                    Orientation = Orientation.Horizontal,
                    Spacing = 10,
                    Children = { fontSizeLabel, fontSizeCombo }
                };

                var btnOk = new Button { Content = LocalizationManager.Instance["Btn_OK"], IsDefault = true, Tag = true };
                var btnCancel = new Button { Content = LocalizationManager.Instance["Btn_Cancel"], IsCancel = true, Tag = false };

                var window = new Window
                {
                    Title = title,
                    MinWidth = 400,
                    MinHeight = 200,
                    SizeToContent = SizeToContent.WidthAndHeight,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Content = new StackPanel
                    {
                        Margin = new Thickness(20),
                        Children =
                        {
                            new TextBlock { Text = LocalizationManager.Instance["Dialog_Text"], Margin = new Thickness(0,0,0,5) },
                            input,
                            fontPanel,
                            new StackPanel 
                            { 
                                Orientation = Orientation.Horizontal, 
                                HorizontalAlignment = HorizontalAlignment.Right, 
                                Margin = new Thickness(0, 20, 0, 0),
                                Spacing = 10,
                                Children = { btnOk, btnCancel }
                            }
                        }
                    }
                };
                
                (string Text, double FontSize)? result = null;

                btnOk.Click += (_, _) => 
                { 
                    double fs = (double?)fontSizeCombo.SelectedItem ?? 12.0;
                    result = (input.Text ?? "", fs); 
                    window.Close(); 
                };
                btnCancel.Click += (_, _) => { result = null; window.Close(); };

                await window.ShowDialog(desktop.MainWindow!);
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
                var dialog = new Views.Dialogs.DrawingSettingsDialog(drawing);
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
                var vm = new IndicatorSettingsDialogViewModel(this, indicatorFactory, toastService);
                vm.OnApplyCallback = onApply;
                vm.Initialize(currentIndicators);
                settingsWindow.DataContext = vm;
                await settingsWindow.ShowDialog(desktop.MainWindow!);
            });
        }
    }

    public async Task ShowIndicatorPropertiesDialogAsync(CoreIndicatorSettings indicator, Action<CoreIndicatorSettings>? onApply = null)
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            await global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var dialog = new Views.Dialogs.IndicatorPropertiesDialog();
                var messenger = _serviceProvider?.GetRequiredService<IMessenger>() ?? WeakReferenceMessenger.Default;
                var dispatcher = _serviceProvider?.GetRequiredService<StockAnalyzer.Core.Services.IDispatcherService>() 
                               ?? new StockAnalyzer.Avalonia.Services.DispatcherService();
                var vm = new IndicatorPropertiesViewModel(indicator, messenger, dispatcher);
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

    public async Task<List<string>?> ShowColumnChooserDialogAsync(IEnumerable<WatchlistColumnMetadata> allColumns, IEnumerable<string> activeColumns)
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            return await global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var messenger = _serviceProvider?.GetRequiredService<IMessenger>() ?? WeakReferenceMessenger.Default;
                var vm = new ColumnChooserViewModel(allColumns, activeColumns, messenger);
                var window = new ColumnChooserWindow
                {
                    DataContext = vm
                };
                
                var parent = desktop.MainWindow;
                var result = await window.ShowDialog<bool>(parent!);
                return result ? vm.GetActiveColumnNames() : null;
            });
        }
        return null;
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

    public async Task<string?> ShowOpenFileDialogAsync(string title, string[]? filters = null)
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null)
        {
            return await global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync<string?>(async () =>
            {
                var topLevel = TopLevel.GetTopLevel(desktop.MainWindow);
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
                    return files[0].Path.LocalPath;
                }
                return null;
            });
        }
        return null;
    }

    public async Task<string?> ShowSaveFileDialogAsync(string title, string defaultExtension = "", string defaultFilename = "", string[]? filters = null)
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null)
        {
            return await global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync<string?>(async () =>
            {
                var topLevel = TopLevel.GetTopLevel(desktop.MainWindow);
                if (topLevel == null) return null;

                var options = new FilePickerSaveOptions
                {
                    Title = title,
                    DefaultExtension = defaultExtension.TrimStart('.'),
                    SuggestedFileName = defaultFilename
                };

                if (filters != null && filters.Length > 0)
                {
                    var fileType = new FilePickerFileType("Files")
                    {
                        Patterns = filters.Select(f => $"*.{f.TrimStart('.')}").ToArray()
                    };
                    options.FileTypeChoices = new[] { fileType };
                }

                var file = await topLevel.StorageProvider.SaveFilePickerAsync(options);
                return file?.Path.LocalPath;
            });
        }
        return null;
    }

    public async Task<StockAnalyzer.Core.Services.PythonSetupDecision> ShowPythonSetupConfirmationAsync()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
             return await global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
             {
                var bgSecondaryBrush = Application.Current!.FindResource("Brush.Background.Secondary") as IBrush ?? Brushes.Black;
                var textPrimaryBrush = Application.Current!.FindResource("Brush.Text.Primary") as IBrush ?? Brushes.White;
                var borderPrimaryBrush = Application.Current!.FindResource("Brush.Border.Primary") as IBrush ?? Brushes.DarkGray;

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

                var message = LocalizationManager.Instance["PythonSetup_Message"] ?? "Python environment is not configured.";

                var window = new Window
                {
                    Title = LocalizationManager.Instance["PythonSetup_Title"] ?? "Python Setup",
                    MinWidth = 500,
                    MinHeight = 200,
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
                            Spacing = 24,
                            Children =
                            {
                                new TextBlock 
                                { 
                                    Text = message, 
                                    TextWrapping = TextWrapping.Wrap,
                                    Foreground = textPrimaryBrush,
                                    FontSize = GetBaseFontSize(),
                                    MaxWidth = 450
                                },
                                new StackPanel
                                {
                                    Orientation = Orientation.Horizontal,
                                    HorizontalAlignment = HorizontalAlignment.Right,
                                    Spacing = 12,
                                    Children = { btnAuto, btnManual, btnCancel }
                                }
                            }
                        }
                    }
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
                var bgSecondaryBrush = Application.Current!.FindResource("Brush.Background.Secondary") as IBrush ?? Brushes.Black;
                var textPrimaryBrush = Application.Current!.FindResource("Brush.Text.Primary") as IBrush ?? Brushes.White;
                var borderPrimaryBrush = Application.Current!.FindResource("Brush.Border.Primary") as IBrush ?? Brushes.DarkGray;

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

                var window = new Window
                {
                    Title = LocalizationManager.Instance["PythonSetup_Manual_Title"] ?? "Manual Setup Instructions",
                    MinWidth = 500,
                    MinHeight = 280,
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
                            Spacing = 24,
                            Children =
                            {
                                new TextBlock 
                                { 
                                    Text = LocalizationManager.Instance["PythonSetup_Manual_Instructions"] ?? "", 
                                    TextWrapping = TextWrapping.Wrap,
                                    Foreground = textPrimaryBrush,
                                    FontSize = GetBaseFontSize(),
                                    MaxWidth = 480
                                },
                                new StackPanel
                                {
                                    Orientation = Orientation.Horizontal,
                                    HorizontalAlignment = HorizontalAlignment.Right,
                                    Children = { btnOk }
                                }
                            }
                        }
                    }
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
                var bgSecondaryBrush = Application.Current!.FindResource("Brush.Background.Secondary") as IBrush ?? Brushes.Black;
                var textPrimaryBrush = Application.Current!.FindResource("Brush.Text.Primary") as IBrush ?? Brushes.White;
                var borderPrimaryBrush = Application.Current!.FindResource("Brush.Border.Primary") as IBrush ?? Brushes.DarkGray;

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

                var message = LocalizationManager.Instance["PythonUpdate_Message"] ?? "Python environment is configured. Would you like to check for and install updates for the required Python libraries?";

                var window = new Window
                {
                    Title = LocalizationManager.Instance["PythonUpdate_Title"] ?? "Python Library Update",
                    MinWidth = 500,
                    MinHeight = 200,
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
                            Spacing = 24,
                            Children =
                            {
                                new TextBlock 
                                { 
                                    Text = message, 
                                    TextWrapping = TextWrapping.Wrap,
                                    Foreground = textPrimaryBrush,
                                    FontSize = Application.Current!.FindResource("HelperFontSize") as double? ?? 12.0,
                                    MaxWidth = 450
                                },
                                new StackPanel
                                {
                                    Orientation = Orientation.Horizontal,
                                    HorizontalAlignment = HorizontalAlignment.Right,
                                    Spacing = 12,
                                    Children = { btnAuto, btnManual, btnCancel }
                                }
                            }
                        }
                    }
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
                var bgSecondaryBrush = Application.Current!.FindResource("Brush.Background.Secondary") as IBrush ?? Brushes.Black;
                var textPrimaryBrush = Application.Current!.FindResource("Brush.Text.Primary") as IBrush ?? Brushes.White;
                var borderPrimaryBrush = Application.Current!.FindResource("Brush.Border.Primary") as IBrush ?? Brushes.DarkGray;

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

                var window = new Window
                {
                    Title = LocalizationManager.Instance["PythonUpdate_Manual_Title"] ?? "Manual Update Instructions",
                    MinWidth = 500,
                    MinHeight = 280,
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
                            Spacing = 24,
                            Children =
                            {
                                new TextBlock 
                                { 
                                    Text = LocalizationManager.Instance["PythonUpdate_Manual_Instructions"] ?? "", 
                                    TextWrapping = TextWrapping.Wrap,
                                    Foreground = textPrimaryBrush,
                                    FontSize = GetBaseFontSize(),
                                    MaxWidth = 480
                                },
                                new StackPanel
                                {
                                    Orientation = Orientation.Horizontal,
                                    HorizontalAlignment = HorizontalAlignment.Right,
                                    Children = { btnOk }
                                }
                            }
                        }
                    }
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

