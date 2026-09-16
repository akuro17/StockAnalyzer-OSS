using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Services.Export;
using StockAnalyzer.Avalonia.Views.Chart;
using StockAnalyzer.Avalonia.Views.Chart.Renderers;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Export;
using StockAnalyzer.Core.Models.Templates;
using StockAnalyzer.Core.Services.Export;
using StockAnalyzer.Core.Theme;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs;

/// <summary>
/// Represents a selectable theme option in the chart export dialog.
/// </summary>
public record ExportThemeOption(string Name, ThemeColors Colors, AppThemeMode Mode, bool IsCustom = false)
{
    public override string ToString() => Name;
}

/// <summary>
/// ViewModel for the Chart Image Export Dialog.
/// Manages export options, folder and file naming, 5 size modes with aspect-ratio preservation,
/// custom theme selection, and live thumbnail preview.
/// </summary>
public partial class ExportChartImageDialogViewModel : ViewModelBase, IDisposable
{
    private readonly ChartDataSnapshot _snapshot;
    private readonly ChartLayoutContext _layout;
    private readonly ICoordinateTransform _transform;
    private readonly ChartObjectManager _objectManager;
    private readonly IChartRenderer _mainRenderer;
    private readonly IChartRenderConfig _renderConfig;
    private readonly ChartType _chartType;
    private readonly RulerRenderer _rulerRenderer;
    private readonly IThemeManager _themeManager;
    private readonly IChartImageExportService _exportService;
    private readonly IDialogService _dialogService;
    private readonly ITemplateService? _templateService;
    private readonly ILogger<ExportChartImageDialogViewModel> _logger;

    private CancellationTokenSource? _previewCts;
    private bool _isDisposed;
    private bool _isUpdatingCustomDimensions;

    public Action<bool>? RequestClose { get; set; }

    public IReadOnlyList<ImageExportFormat> AvailableFormats { get; } =
    [
        ImageExportFormat.Png,
        ImageExportFormat.Jpeg,
        ImageExportFormat.Webp
    ];

    public IReadOnlyList<ChartExportSizeMode> AvailableSizeModes { get; } =
    [
        ChartExportSizeMode.CurrentWindow,
        ChartExportSizeMode.Preset1280x720,
        ChartExportSizeMode.Preset1920x1080,
        ChartExportSizeMode.Preset3840x2160,
        ChartExportSizeMode.Custom
    ];

    public ObservableCollection<ExportThemeOption> AvailableThemes { get; } = new();

    [ObservableProperty]
    private string _symbol = string.Empty;

    [ObservableProperty]
    private string _companyName = string.Empty;

    [ObservableProperty]
    private string _timeframe = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilePath))]
    private string _saveDirectory = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilePath))]
    private string _fileName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilePath))]
    private ImageExportFormat _selectedFormat = ImageExportFormat.Png;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomSize))]
    private ChartExportSizeMode _selectedSizeMode = ChartExportSizeMode.CurrentWindow;

    [ObservableProperty]
    private int _customWidth = 1280;

    [ObservableProperty]
    private int _customHeight = 720;

    [ObservableProperty]
    private bool _lockAspectRatio = true;

    [ObservableProperty]
    private int _quality = 90;

    [ObservableProperty]
    private ExportThemeOption? _selectedTheme;

    [ObservableProperty]
    private bool _includeVisualHeader = true;

    [ObservableProperty]
    private bool _includeSymbol = true;

    [ObservableProperty]
    private bool _includeCompanyName = true;

    [ObservableProperty]
    private bool _includeTimeframe = true;

    [ObservableProperty]
    private bool _includeDateRange = true;

    [ObservableProperty]
    private bool _includeIndicators = true;

    [ObservableProperty]
    private bool _includeBrand = true;

    [ObservableProperty]
    private bool _embedFileMetadata = true;

    [ObservableProperty]
    private bool _includeCrosshair = false;

    [ObservableProperty]
    private Bitmap? _previewImage;

    [ObservableProperty]
    private bool _isLoadingPreview;

    [ObservableProperty]
    private bool _isExporting;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private string _outputDimensionsText = string.Empty;

    public bool IsQualityVisible => SelectedFormat != ImageExportFormat.Png;
    public bool IsCustomSize => SelectedSizeMode == ChartExportSizeMode.Custom;

    public string FilePath
    {
        get
        {
            try
            {
                if (string.IsNullOrWhiteSpace(SaveDirectory))
                {
                    return $"{(FileName ?? string.Empty)}{ChartExportFileNameGenerator.GetFileExtension(SelectedFormat)}";
                }
                return Path.Combine(SaveDirectory, $"{(FileName ?? string.Empty)}{ChartExportFileNameGenerator.GetFileExtension(SelectedFormat)}");
            }
            catch
            {
                return $"{SaveDirectory}\\{(FileName ?? string.Empty)}{ChartExportFileNameGenerator.GetFileExtension(SelectedFormat)}";
            }
        }
        set
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                try
                {
                    var dir = Path.GetDirectoryName(value);
                    var fn = Path.GetFileNameWithoutExtension(value);
                    if (!string.IsNullOrEmpty(dir)) SaveDirectory = dir;
                    if (!string.IsNullOrEmpty(fn)) FileName = fn;
                }
                catch
                {
                    // Ignore path parsing errors
                }
            }
        }
    }

    public ExportChartImageDialogViewModel(
        ChartDataSnapshot snapshot,
        ChartLayoutContext layout,
        ICoordinateTransform transform,
        ChartObjectManager objectManager,
        IChartRenderer mainRenderer,
        IChartRenderConfig renderConfig,
        ChartType chartType,
        RulerRenderer rulerRenderer,
        IThemeManager themeManager,
        IChartImageExportService exportService,
        IDialogService dialogService,
        ITemplateService? templateService = null,
        string symbol = "",
        string companyName = "",
        string timeframe = "",
        ILogger<ExportChartImageDialogViewModel>? logger = null)
    {
        _snapshot = snapshot;
        _layout = layout;
        _transform = transform;
        _objectManager = objectManager;
        _mainRenderer = mainRenderer;
        _renderConfig = renderConfig;
        _chartType = chartType;
        _rulerRenderer = rulerRenderer;
        _themeManager = themeManager;
        _exportService = exportService;
        _dialogService = dialogService;
        _templateService = templateService;
        _logger = logger ?? NullLogger<ExportChartImageDialogViewModel>.Instance;

        _symbol = string.IsNullOrWhiteSpace(symbol) ? "CHART" : symbol;
        _companyName = companyName;
        _timeframe = string.IsNullOrWhiteSpace(timeframe) ? "Daily" : timeframe;

        // Default to Data\Notes\Attachments
        _saveDirectory = ChartExportFileNameGenerator.GetDefaultExportDirectory();

        var defaultFullName = ChartExportFileNameGenerator.GenerateFileName(
            _symbol, _companyName, DateTime.Now, SelectedFormat);
        _fileName = Path.GetFileNameWithoutExtension(defaultFullName);

        double baseWidth = _layout.TotalBounds.Width > 0 ? _layout.TotalBounds.Width : 800;
        double baseHeight = _layout.TotalBounds.Height > 0 ? _layout.TotalBounds.Height : 600;
        float headerHeight = IncludeVisualHeader ? 52f : 0f;
        _customWidth = (int)Math.Round(baseWidth);
        _customHeight = (int)Math.Round(baseHeight + headerHeight);

        // Pre-populate built-in themes immediately
        var darkPreset = new ExportThemeOption("Dark", ThemeColors.Dark, AppThemeMode.Dark);
        var lightPreset = new ExportThemeOption("Light", ThemeColors.Light, AppThemeMode.Light);
        AvailableThemes.Add(darkPreset);
        AvailableThemes.Add(lightPreset);
        _selectedTheme = _themeManager.CurrentMode == AppThemeMode.Light ? lightPreset : darkPreset;

        UpdateDimensions();
    }

    public async Task InitializeAsync()
    {
        if (_templateService != null)
        {
            try
            {
                var customTemplates = await _templateService.GetAllAsync<ThemeTemplate>(TemplateType.Theme);
                foreach (var template in customTemplates)
                {
                    if (template.Colors != null && !AvailableThemes.Any(t => t.Name.Equals(template.Name, StringComparison.OrdinalIgnoreCase)))
                    {
                        AvailableThemes.Add(new ExportThemeOption(template.Name, template.Colors, AppThemeMode.Dark, true));
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load custom theme templates for export.");
            }
        }

        UpdateDimensions();
        await RequestPreviewUpdateAsync();
    }

    partial void OnSelectedFormatChanged(ImageExportFormat value)
    {
        OnPropertyChanged(nameof(IsQualityVisible));
        OnPropertyChanged(nameof(FilePath));
        _ = RequestPreviewUpdateAsync();
    }

    partial void OnSelectedSizeModeChanged(ChartExportSizeMode value)
    {
        OnPropertyChanged(nameof(IsCustomSize));
        UpdateDimensions();
        _ = RequestPreviewUpdateAsync();
    }

    partial void OnCustomWidthChanged(int value)
    {
        if (_isUpdatingCustomDimensions || !LockAspectRatio)
        {
            UpdateDimensions();
            _ = RequestPreviewUpdateAsync();
            return;
        }

        double baseWidth = _layout.TotalBounds.Width > 0 ? _layout.TotalBounds.Width : 800;
        double baseHeight = _layout.TotalBounds.Height > 0 ? _layout.TotalBounds.Height : 600;
        float headerHeight = IncludeVisualHeader ? 52f : 0f;
        double totalBaseHeight = baseHeight + headerHeight;
        double ratio = baseWidth / totalBaseHeight;

        _isUpdatingCustomDimensions = true;
        try
        {
            CustomHeight = Math.Max(1, (int)Math.Round(value / ratio));
        }
        finally
        {
            _isUpdatingCustomDimensions = false;
        }

        UpdateDimensions();
        _ = RequestPreviewUpdateAsync();
    }

    partial void OnCustomHeightChanged(int value)
    {
        if (_isUpdatingCustomDimensions || !LockAspectRatio)
        {
            UpdateDimensions();
            _ = RequestPreviewUpdateAsync();
            return;
        }

        double baseWidth = _layout.TotalBounds.Width > 0 ? _layout.TotalBounds.Width : 800;
        double baseHeight = _layout.TotalBounds.Height > 0 ? _layout.TotalBounds.Height : 600;
        float headerHeight = IncludeVisualHeader ? 52f : 0f;
        double totalBaseHeight = baseHeight + headerHeight;
        double ratio = baseWidth / totalBaseHeight;

        _isUpdatingCustomDimensions = true;
        try
        {
            CustomWidth = Math.Max(1, (int)Math.Round(value * ratio));
        }
        finally
        {
            _isUpdatingCustomDimensions = false;
        }

        UpdateDimensions();
        _ = RequestPreviewUpdateAsync();
    }

    partial void OnLockAspectRatioChanged(bool value)
    {
        if (value)
        {
            OnCustomWidthChanged(CustomWidth);
        }
    }

    partial void OnSelectedThemeChanged(ExportThemeOption? value)
    {
        _ = RequestPreviewUpdateAsync();
    }

    partial void OnIncludeVisualHeaderChanged(bool value)
    {
        UpdateDimensions();
        _ = RequestPreviewUpdateAsync();
    }

    partial void OnIncludeSymbolChanged(bool value) => _ = RequestPreviewUpdateAsync();
    partial void OnIncludeCompanyNameChanged(bool value) => _ = RequestPreviewUpdateAsync();
    partial void OnIncludeTimeframeChanged(bool value) => _ = RequestPreviewUpdateAsync();
    partial void OnIncludeDateRangeChanged(bool value) => _ = RequestPreviewUpdateAsync();
    partial void OnIncludeIndicatorsChanged(bool value) => _ = RequestPreviewUpdateAsync();
    partial void OnIncludeBrandChanged(bool value) => _ = RequestPreviewUpdateAsync();

    partial void OnIncludeCrosshairChanged(bool value)
    {
        _ = RequestPreviewUpdateAsync();
    }

    public (int Width, int Height, float Scale) CalculateOutputDimensions()
    {
        double baseWidth = _layout.TotalBounds.Width > 0 ? _layout.TotalBounds.Width : 800;
        double baseHeight = _layout.TotalBounds.Height > 0 ? _layout.TotalBounds.Height : 600;
        float headerHeight = IncludeVisualHeader ? 52f : 0f;
        double totalBaseHeight = baseHeight + headerHeight;

        switch (SelectedSizeMode)
        {
            case ChartExportSizeMode.Preset1280x720:
            {
                double scale = Math.Min(1280.0 / baseWidth, 720.0 / totalBaseHeight);
                int w = (int)Math.Max(1, Math.Round(baseWidth * scale));
                int h = (int)Math.Max(1, Math.Round(totalBaseHeight * scale));
                return (w, h, (float)scale);
            }
            case ChartExportSizeMode.Preset1920x1080:
            {
                double scale = Math.Min(1920.0 / baseWidth, 1080.0 / totalBaseHeight);
                int w = (int)Math.Max(1, Math.Round(baseWidth * scale));
                int h = (int)Math.Max(1, Math.Round(totalBaseHeight * scale));
                return (w, h, (float)scale);
            }
            case ChartExportSizeMode.Preset3840x2160:
            {
                double scale = Math.Min(3840.0 / baseWidth, 2160.0 / totalBaseHeight);
                int w = (int)Math.Max(1, Math.Round(baseWidth * scale));
                int h = (int)Math.Max(1, Math.Round(totalBaseHeight * scale));
                return (w, h, (float)scale);
            }
            case ChartExportSizeMode.Custom:
            {
                int w = Math.Max(1, CustomWidth);
                int h = Math.Max(1, CustomHeight);
                double scale = Math.Min((double)w / baseWidth, (double)h / totalBaseHeight);
                return (w, h, (float)scale);
            }
            case ChartExportSizeMode.CurrentWindow:
            default:
            {
                int w = (int)Math.Max(1, Math.Round(baseWidth));
                int h = (int)Math.Max(1, Math.Round(totalBaseHeight));
                return (w, h, 1.0f);
            }
        }
    }

    private void UpdateDimensions()
    {
        var (pixelWidth, pixelHeight, _) = CalculateOutputDimensions();
        OutputDimensionsText = $"{pixelWidth} × {pixelHeight} px";
    }

    private async Task RequestPreviewUpdateAsync()
    {
        _previewCts?.Cancel();
        _previewCts = new CancellationTokenSource();
        var ct = _previewCts.Token;

        try
        {
            IsLoadingPreview = true;
            await Task.Delay(50, ct); // Debounce

            var options = BuildExportOptions();
            var metadata = BuildMetadata();

            var previewBytes = await _exportService.RenderChartPreviewAsync(
                _snapshot, _layout, _transform, options, _themeManager,
                _objectManager, _mainRenderer, _renderConfig, _chartType, _rulerRenderer, metadata, 480);

            if (ct.IsCancellationRequested || previewBytes == null || previewBytes.Length == 0) return;

            using var stream = new MemoryStream(previewBytes);
            var oldImage = PreviewImage;
            PreviewImage = new Bitmap(stream);
            oldImage?.Dispose();
        }
        catch (OperationCanceledException)
        {
            // Expected on fast parameter changes
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to render chart export preview.");
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                IsLoadingPreview = false;
            }
        }
    }

    [RelayCommand]
    private async Task BrowseFolderAsync()
    {
        try
        {
            var picked = await _dialogService.ShowOpenFolderDialogAsync(
                "Select Save Folder",
                SaveDirectory);

            if (!string.IsNullOrWhiteSpace(picked))
            {
                SaveDirectory = picked;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to browse save folder.");
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(SaveDirectory) || string.IsNullOrWhiteSpace(FileName))
        {
            StatusMessage = "Please select a valid destination folder and file name.";
            return;
        }

        var ext = ChartExportFileNameGenerator.GetFileExtension(SelectedFormat);
        var fullPath = Path.Combine(SaveDirectory, $"{FileName}{ext}");

        if (File.Exists(fullPath))
        {
            var title = LocalizationManager.Instance["Dialog_Confirm_Title"] ?? "Confirmation";
            var template = LocalizationManager.Instance["Dialog_ExportChartImage_OverwriteConfirm"]
                ?? "A file named '{0}' already exists. Do you want to overwrite it?";
            var message = string.Format(template, Path.GetFileName(fullPath));

            var confirmed = await _dialogService.ShowConfirmationAsync(title, message);
            if (!confirmed)
            {
                StatusMessage = "Save cancelled.";
                return;
            }
        }

        try
        {
            IsExporting = true;
            StatusMessage = "Exporting chart image...";

            var options = BuildExportOptions();
            var metadata = BuildMetadata();

            var imageBytes = await _exportService.RenderChartImageAsync(
                _snapshot, _layout, _transform, options, _themeManager,
                _objectManager, _mainRenderer, _renderConfig, _chartType, _rulerRenderer, metadata);

            if (imageBytes == null || imageBytes.Length == 0)
            {
                StatusMessage = "Failed to render chart image.";
                return;
            }

            var success = await _exportService.ExportToFileAsync(fullPath, imageBytes);
            if (success)
            {
                RequestClose?.Invoke(true);
            }
            else
            {
                StatusMessage = "Failed to save file to disk.";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed during chart image save.");
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsExporting = false;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        RequestClose?.Invoke(false);
    }

    private ChartImageExportOptions BuildExportOptions()
    {
        var (w, h, scale) = CalculateOutputDimensions();

        return new ChartImageExportOptions
        {
            Format = SelectedFormat,
            Scale = scale,
            SizeMode = SelectedSizeMode,
            Quality = Quality,
            ThemeMode = SelectedTheme?.Mode == AppThemeMode.Light ? ChartImageExportThemeMode.Light : ChartImageExportThemeMode.Dark,
            OverrideThemeColors = SelectedTheme?.Colors,
            IncludeVisualHeader = IncludeVisualHeader,
            IncludeSymbol = IncludeSymbol,
            IncludeCompanyName = IncludeCompanyName,
            IncludeTimeframe = IncludeTimeframe,
            IncludeDateRange = IncludeDateRange,
            IncludeIndicators = IncludeIndicators,
            IncludeBrand = IncludeBrand,
            EmbedFileMetadata = EmbedFileMetadata,
            IncludeCrosshair = IncludeCrosshair,
            CustomWidth = SelectedSizeMode == ChartExportSizeMode.Custom ? w : null,
            CustomHeight = SelectedSizeMode == ChartExportSizeMode.Custom ? h : null
        };
    }

    private ChartImageMetadata BuildMetadata()
    {
        DateTime? startDate = _snapshot.Candles.Count > 0 ? _snapshot.Candles[0].Timestamp : null;
        DateTime? endDate = _snapshot.Candles.Count > 0 ? _snapshot.Candles[^1].Timestamp : null;

        var indicatorNames = _snapshot.IndicatorSettings?
            .Where(s => s.IsEnabled)
            .Select(s => !string.IsNullOrEmpty(s.DisplayName) ? s.DisplayName : (s.TypeEnum?.ToString() ?? "Indicator"))
            .ToList();

        var indSummary = indicatorNames != null && indicatorNames.Count > 0
            ? string.Join(", ", indicatorNames)
            : string.Empty;

        return new ChartImageMetadata
        {
            Symbol = Symbol,
            CompanyName = CompanyName,
            Timeframe = Timeframe,
            StartDate = startDate,
            EndDate = endDate,
            IndicatorsSummary = indSummary,
            GeneratedAt = DateTime.Now
        };
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _previewCts?.Cancel();
        _previewCts?.Dispose();
        PreviewImage?.Dispose();
    }
}
