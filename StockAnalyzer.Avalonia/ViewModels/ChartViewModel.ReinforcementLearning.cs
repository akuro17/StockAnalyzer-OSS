using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.ReinforcementLearning;

namespace StockAnalyzer.Avalonia.ViewModels;

public partial class ChartViewModel
{
    private bool _applyingRlReplay;
    internal bool IsApplyingRlReplay => _applyingRlReplay;
    private long _rlChartRevision;
    private RlPresentation? _rlPresentation;
    public RlPresentation? RlPresentation { get => _rlPresentation; private set { SetProperty(ref _rlPresentation, value); OnPropertyChanged(nameof(HasRlPresentation)); } }
    public bool HasRlPresentation => RlPresentation is not null;
    public void ClearRlPresentation()
    {
        ++_rlChartRevision;
        if (RlPresentation is null) return;
        RlPresentation = null;
        RequestRender(RenderReason.VisualChanged);
    }
    public async Task ApplyRlReplayAsync(RlQSnapshot snapshot, StockAnalyzer.Core.Interfaces.ILocalizationService localization, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Account.Terminated || snapshot.Account.MarketWindow.Length != RlContract.WindowLength
            || snapshot.Account.MarketWindow[^1].Timestamp != snapshot.Account.MarketAnchor)
            throw new ArgumentException("A completed nonterminal Core market observation is required.", nameof(snapshot));
        long revision = ++_rlChartRevision;
        _loadCts?.Cancel();
        await _dispatcherService.PostAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_isDisposed || revision != _rlChartRevision) return;
            if (_drawingRepository is not null && _drawingsOwnerSymbol is not null)
            {
                var outgoing = ObjectManager.GetSnapshot();
                if (!IsDrawingsStale() && ShouldPersistDrawingsSnapshot(outgoing))
                    await SaveDrawingsIfCurrent(_drawingsOwnerSymbol, _drawingsOwnerTimeFrame, BuildDrawingsPayload(outgoing)).Completion;
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (_isDisposed || revision != _rlChartRevision) return;
            _applyingRlReplay = true;
            try
            {
                _loadCts?.Cancel(); ++_predictionRequestId; CurrentPrediction = null; PredictionDisplay = null; ClearModelAnalysis();
                Symbol = snapshot.Symbol; SelectedTimeFrame = TimeframeType.Daily;
                ReloadDrawingsFromRepository(Symbol, SelectedTimeFrame);
                _drawingsOwnerSymbol = Symbol; _drawingsOwnerTimeFrame = SelectedTimeFrame; UpdateDrawingCoordinator();
                Candles = snapshot.Account.MarketWindow.Select(b => new CoreCandleData(b.Timestamp, b.Open, b.High, b.Low, b.Close, b.Volume)).ToArray(); DataRevision++;
                _extendedCalculationCandles = null; IndicatorResults = null;
                CurrentPrice = Candles[^1].Close; VisibleStartIndex = 0;
                RlPresentation = ViewModels.RlPresentation.From(snapshot, localization);
                CalculateIndicatorsAsync(); RequestRender(RenderReason.DataChanged);
            }
            finally { _applyingRlReplay = false; }
        });
    }
}
