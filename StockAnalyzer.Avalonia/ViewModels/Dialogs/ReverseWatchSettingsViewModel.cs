using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Theme;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs;

public partial class ReverseWatchSettingsViewModel : BaseChartSettingsViewModel
{
    public override string TitleKey => "ChartType_ReverseWatch";
    public override string IconKey => "SettingsChartIcon";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private int _period;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private int _dataCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private bool _isMaBased;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private bool _isLogScaleVolume;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private bool _showGrid;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private float _lineThickness;

    [ObservableProperty]
    private ObservableCollection<HsvData> _phaseColors = new();

    private List<HsvData> _initialPhaseColors = new();

    public override bool IsModified
    {
        get
        {
            if (Period != Snapshot.ReverseWatchPeriod ||
                DataCount != Snapshot.ReverseWatchDataCount ||
                IsMaBased != Snapshot.ReverseWatchIsMaBased ||
                IsLogScaleVolume != Snapshot.ReverseWatchIsLogScaleVolume ||
                ShowGrid != Snapshot.ShowReverseWatchGrid ||
                Math.Abs(LineThickness - Snapshot.ReverseWatchLineThickness) > 0.01f ||
                PhaseColors.Count != _initialPhaseColors.Count)
            {
                return true;
            }

            for (int i = 0; i < PhaseColors.Count; i++)
            {
                if (PhaseColors[i] != _initialPhaseColors[i])
                    return true;
            }

            return false;
        }
    }

    public ReverseWatchSettingsViewModel(IChartSettingsManager settingsManager, ILogger<ReverseWatchSettingsViewModel>? logger = null)
        : base(settingsManager, logger)
    {
        PhaseColors.CollectionChanged += OnPhaseColorsChanged;
    }

    private void OnPhaseColorsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(IsModified));
    }

    protected override void LoadFromSettings(GlobalChartSettings settings)
    {
        Period = settings.ReverseWatchPeriod;
        DataCount = settings.ReverseWatchDataCount;
        IsMaBased = settings.ReverseWatchIsMaBased;
        IsLogScaleVolume = settings.ReverseWatchIsLogScaleVolume;
        ShowGrid = settings.ShowReverseWatchGrid;
        LineThickness = settings.ReverseWatchLineThickness;

        var colors = new List<HsvData>
        {
            HsvData.FromHtmlSafe(settings.ReverseWatchPhase1Color, HsvData.FromHtmlSafe(ChartSettingsConstants.DefaultReverseWatchPhase1)),
            HsvData.FromHtmlSafe(settings.ReverseWatchPhase2Color, HsvData.FromHtmlSafe(ChartSettingsConstants.DefaultReverseWatchPhase2)),
            HsvData.FromHtmlSafe(settings.ReverseWatchPhase3Color, HsvData.FromHtmlSafe(ChartSettingsConstants.DefaultReverseWatchPhase3)),
            HsvData.FromHtmlSafe(settings.ReverseWatchPhase4Color, HsvData.FromHtmlSafe(ChartSettingsConstants.DefaultReverseWatchPhase4)),
            HsvData.FromHtmlSafe(settings.ReverseWatchPhase5Color, HsvData.FromHtmlSafe(ChartSettingsConstants.DefaultReverseWatchPhase5)),
            HsvData.FromHtmlSafe(settings.ReverseWatchPhase6Color, HsvData.FromHtmlSafe(ChartSettingsConstants.DefaultReverseWatchPhase6)),
            HsvData.FromHtmlSafe(settings.ReverseWatchPhase7Color, HsvData.FromHtmlSafe(ChartSettingsConstants.DefaultReverseWatchPhase7)),
            HsvData.FromHtmlSafe(settings.ReverseWatchPhase8Color, HsvData.FromHtmlSafe(ChartSettingsConstants.DefaultReverseWatchPhase8))
        };

        // Suppress notification during load if possible, or just replace
        PhaseColors.CollectionChanged -= OnPhaseColorsChanged;
        PhaseColors.Clear();
        foreach (var color in colors)
        {
            PhaseColors.Add(color);
        }
        PhaseColors.CollectionChanged += OnPhaseColorsChanged;
    }

    protected override void UpdateInitialState()
    {
        _initialPhaseColors = PhaseColors.ToList();
    }

    protected override GlobalChartSettings CreateSettings()
    {
        return Snapshot with
        {
            ReverseWatchPeriod = Period,
            ReverseWatchDataCount = DataCount,
            ReverseWatchIsMaBased = IsMaBased,
            ReverseWatchIsLogScaleVolume = IsLogScaleVolume,
            ShowReverseWatchGrid = ShowGrid,
            ReverseWatchLineThickness = LineThickness,
            ReverseWatchPhase1Color = PhaseColors[0].ToHtml(),
            ReverseWatchPhase2Color = PhaseColors[1].ToHtml(),
            ReverseWatchPhase3Color = PhaseColors[2].ToHtml(),
            ReverseWatchPhase4Color = PhaseColors[3].ToHtml(),
            ReverseWatchPhase5Color = PhaseColors[4].ToHtml(),
            ReverseWatchPhase6Color = PhaseColors[5].ToHtml(),
            ReverseWatchPhase7Color = PhaseColors[6].ToHtml(),
            ReverseWatchPhase8Color = PhaseColors[7].ToHtml()
        };
    }
}
