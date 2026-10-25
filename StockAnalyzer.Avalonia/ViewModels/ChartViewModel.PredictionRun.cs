using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Execution;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services;

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

    /// <summary>
    /// Runs a temporary sandbox prediction using an ephemeral ONNX model directly against current candles without persistence.
    /// </summary>
    public Task<PredictionRunReport> RunTempModelNowAsync(string onnxPath, CancellationToken cancellationToken = default)
    {
        var symbol = Symbol;
        var timeframe = SelectedTimeFrame;
        PredictionRunReport Report(PredictionRunOutcome outcome, string? display = null, string? notice = null) => new(outcome, symbol, timeframe, display, notice, Candles.Count);

        if (_isDisposed || Candles.Count == 0) return Task.FromResult(Report(PredictionRunOutcome.NoData));
        if (string.IsNullOrWhiteSpace(onnxPath) || !File.Exists(onnxPath))
            return Task.FromResult(Report(PredictionRunOutcome.Completed, null, "File not found"));

        try
        {
            var candleList = Candles.Select(c => new CandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume)).ToList();
            using var lease = ArtifactLease.FromPath(onnxPath);
            using var model = EphemeralPredictionModel.Open(lease, new MLDataProcessor(), EphemeralExecutionPolicy.GuiEquivalent, _indicatorFactory);
            var predictions = model.PredictLast(candleList, 1);
            if (predictions.Count == 0)
                return Task.FromResult(Report(PredictionRunOutcome.Completed, null, "No prediction produced"));

            var p = predictions[^1];
            string display = p.PredictedClass is not null
                ? $"{p.PredictedClass} ({(p.Confidence ?? 0f) * 100:F1}%)"
                : $"{p.Value:F4}";
            return Task.FromResult(Report(PredictionRunOutcome.Completed, display, null));
        }
        catch (Exception ex)
        {
            return Task.FromResult(Report(PredictionRunOutcome.Completed, null, ex.Message));
        }
    }
}
