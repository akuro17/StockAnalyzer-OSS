using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using StockAnalyzer.Avalonia.ViewModels;

namespace StockAnalyzer.Avalonia.Services;

/// <summary>Forwards to the main chart of the singleton main window; the chart is resolved on every call and never cached.</summary>
public sealed class MainChartPredictionRunTarget(IServiceProvider services) : IPredictionRunTarget
{
    public Task<PredictionRunReport> RunAsync(CancellationToken cancellationToken)
        => services.GetRequiredService<MainWindowViewModel>().ChartViewModel.RunPredictionNowAsync(cancellationToken);
}
