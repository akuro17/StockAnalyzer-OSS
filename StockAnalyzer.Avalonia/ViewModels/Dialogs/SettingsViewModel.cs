using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Avalonia.Models;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs;

public partial class SettingsViewModel : ViewModelBase, IDisposable
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<SettingsViewModel> _logger;
    private readonly Dictionary<string, ISettingsPageViewModel> _pageCache = new();
    private bool _isSaved;
    private bool _isDisposed;

    public event Action? RequestClose;

    [ObservableProperty]
    private ISettingsPageViewModel? _currentPage;

    [ObservableProperty]
    private SettingsCategory? _selectedCategory;

    public IReadOnlyList<SettingsCategory> Categories => SettingsConstants.Categories;

    public string AppVersion => $"v{Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)}{SettingsConstants.VersionSuffix}";

    public bool IsAnyModified => _pageCache.Values.Any(p => p.IsModified);

    public SettingsViewModel(IServiceProvider serviceProvider, ILogger<SettingsViewModel>? logger = null)
    {
        _serviceProvider = serviceProvider;
        _logger = logger ?? NullLogger<SettingsViewModel>.Instance;

        // Initialize with first category
        SelectedCategory = Categories.FirstOrDefault();
    }

    partial void OnSelectedCategoryChanged(SettingsCategory? value)
    {
        if (_isDisposed || value == null) return;

        // Note: Even if !IsImplemented, we navigate to the placeholder now.
        NavigateTo(value.Key);
    }

    private void NavigateTo(string key)
    {
        if (_isDisposed) return;

        // Check cache first for ALL categories
        if (!_pageCache.TryGetValue(key, out var cachedPage))
        {
            _logger.LogDebug("Resolving settings page for key: {Key}", key);
            
            // Resolve page via DI based on key
            cachedPage = key switch
            {
                // Main Categories
                SettingsConstants.Keys.Theme => _serviceProvider.GetRequiredService<ThemeSettingsViewModel>(),
                SettingsConstants.Keys.Fonts => _serviceProvider.GetRequiredService<FontsSettingsViewModel>(),
                SettingsConstants.Keys.Chart => _serviceProvider.GetRequiredService<ChartGeneralSettingsViewModel>(),
                
                // Chart Sub-categories
                SettingsConstants.Keys.ChartCandlestick => _serviceProvider.GetRequiredService<CandlestickSettingsViewModel>(),
                SettingsConstants.Keys.ChartOhlcBar => _serviceProvider.GetRequiredService<OhlcBarSettingsViewModel>(),
                SettingsConstants.Keys.ChartRenko => _serviceProvider.GetRequiredService<RenkoSettingsViewModel>(),
                SettingsConstants.Keys.ChartKagi => _serviceProvider.GetRequiredService<KagiSettingsViewModel>(),
                SettingsConstants.Keys.ChartPnf => _serviceProvider.GetRequiredService<PnfSettingsViewModel>(),
                SettingsConstants.Keys.ChartLine => _serviceProvider.GetRequiredService<LineSettingsViewModel>(),
                SettingsConstants.Keys.ChartArea => _serviceProvider.GetRequiredService<AreaSettingsViewModel>(),
                SettingsConstants.Keys.ChartHeikinAshi => _serviceProvider.GetRequiredService<HeikinAshiSettingsViewModel>(),
                SettingsConstants.Keys.ChartTlb => _serviceProvider.GetRequiredService<ThreeLineBreakSettingsViewModel>(),
                SettingsConstants.Keys.ChartRelativePerformance => _serviceProvider.GetRequiredService<RelativePerformanceSettingsViewModel>(),
                SettingsConstants.Keys.ChartReverseWatch => _serviceProvider.GetRequiredService<ReverseWatchSettingsViewModel>(),
                
                // Fallback
                _ => _serviceProvider.GetRequiredService<PlaceholderSettingsViewModel>()
            };

            if (cachedPage != null)
            {
                _pageCache[key] = cachedPage;
                cachedPage.PropertyChanged += OnPagePropertyChanged;
            }
        }

        if (cachedPage != null)
        {
            CurrentPage = cachedPage;
        }
    }

    // Removed SetCurrentPage(page, isTransient) as it is no longer needed.
    // All pages are now cached in _pageCache for the duration of the dialog session
    // and are disposed in the SettingsViewModel.Dispose method.

    private void OnPagePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ISettingsPageViewModel.IsModified))
        {
            OnPropertyChanged(nameof(IsAnyModified));
        }
    }

    [RelayCommand]
    private async Task SaveAllChanges()
    {
        if (_isDisposed) return;

        foreach (var page in _pageCache.Values.Where(p => p.IsModified))
        {
            await page.SaveChangesAsync();
        }
        _isSaved = true;
        OnPropertyChanged(nameof(IsAnyModified));
    }

    [RelayCommand]
    private async Task SaveAndClose()
    {
        await SaveAllChanges();
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private void RevertAllChanges()
    {
        if (_isDisposed) return;

        foreach (var page in _pageCache.Values)
        {
            page.RevertChanges();
        }
        OnPropertyChanged(nameof(IsAnyModified));
    }

    [RelayCommand]
    private void DiscardAndClose()
    {
        RevertAllChanges();
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private void ResetCurrentPage()
    {
        if (_isDisposed) return;
        CurrentPage?.ResetToDefault();
    }

    public void OnClosing()
    {
        if (_isDisposed) return;
        if (!_isSaved)
        {
            RevertAllChanges();
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        foreach (var page in _pageCache.Values)
        {
            page.PropertyChanged -= OnPagePropertyChanged;
            if (page is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
        _pageCache.Clear();
        GC.SuppressFinalize(this);
    }
}
