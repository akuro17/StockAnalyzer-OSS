using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models.ReinforcementLearning;

namespace StockAnalyzer.Avalonia.Services;

/// <summary>Forwards to the main chart of the singleton main window; the chart is resolved on every call and never cached.</summary>
public sealed class MainChartRlReplayTarget(IServiceProvider services, ILocalizationService localization) : IRlReplayTarget
{
    private ChartViewModel MainChart => services.GetRequiredService<MainWindowViewModel>().ChartViewModel;

    public Task ApplyReplayAsync(RlQSnapshot snapshot, CancellationToken cancellationToken)
        => MainChart.ApplyRlReplayAsync(snapshot, localization, cancellationToken);

    public void Clear() => MainChart.ClearRlPresentation();
}
