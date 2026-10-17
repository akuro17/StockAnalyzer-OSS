using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Avalonia.Services;

namespace StockAnalyzer.Avalonia.ViewModels;

public partial class ChartViewModel
{
    /// <summary>
    /// The owner asks for a prediction now (Prediction Models window). The chart predicts again for the candles it holds through the same path as every
    /// automatic prediction (<see cref="RefreshPrediction"/>), so the model lease, the window-size check and the monitoring record are the same; this only
    /// waits for the outcome and reports what the chart shows afterwards.
    /// </summary>
    public async Task<PredictionRunReport> RunPredictionNowAsync(CancellationToken cancellationToken = default)
    {
        var symbol = Symbol;
        var timeframe = SelectedTimeFrame;
        PredictionRunReport Report(PredictionRunOutcome outcome, string? display = null, string? notice = null) => new(outcome, symbol, timeframe, display, notice, Candles.Count);

        if (_predictionService is null) return Report(PredictionRunOutcome.Unavailable);
        if (RlPresentation is not null) return Report(PredictionRunOutcome.ReplayShown);
        if (_isDisposed || Candles.Count == 0) return Report(PredictionRunOutcome.NoData);

        RefreshPrediction();
        await PredictionUpdateTask.WaitAsync(cancellationToken);
        // The update posts its result to the UI thread; an empty posted action runs after it, so the properties read below are the new ones.
        await _dispatcherService.PostAsync(() => Task.CompletedTask);
        return Report(PredictionRunOutcome.Completed, PredictionDisplay, PredictionNotice);
    }
}
