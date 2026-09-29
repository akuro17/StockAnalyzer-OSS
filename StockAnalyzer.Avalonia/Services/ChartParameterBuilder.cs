using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Strategies;

namespace StockAnalyzer.Avalonia.Services;

/// <summary>
/// Builds <see cref="ChartStrategyParameters"/> from ChartViewModel state.
/// Extracts chart-type-specific parameter mapping from ViewModel (SRP).
/// </summary>
public static class ChartParameterBuilder
{
    /// <summary>
    /// Creates strategy parameters based on the current chart type and ViewModel settings.
    /// </summary>
    public static ChartStrategyParameters Build(ChartType chartType, ChartViewModel viewModel)
    {
        return chartType switch
        {
            ChartType.Kagi => new ChartStrategyParameters(
                viewModel.KagiReversalMode,
                viewModel.KagiReversalAmount,
                viewModel.KagiAtrPeriod,
                viewModel.KagiAtrMultiplier,
                viewModel.KagiReversalPercent,
                3, // PnfReversal default
                2, // RenkoReversal default
                viewModel.KagiRoundingMode,
                viewModel.KagiFallbackMode),

            ChartType.Renko => new ChartStrategyParameters(
                viewModel.RenkoSizingMode,
                viewModel.RenkoBrickSize,
                viewModel.RenkoAtrPeriod,
                viewModel.RenkoAtrMultiplier,
                viewModel.RenkoBrickPercent,
                3, // PnfReversal default
                viewModel.RenkoReversal,
                viewModel.RenkoRoundingMode,
                viewModel.RenkoFallbackMode),

            ChartType.PointAndFigure => new ChartStrategyParameters(
                viewModel.PnfSizingMode,
                viewModel.PnfBoxSize,
                viewModel.PnfAtrPeriod,
                viewModel.PnfAtrMultiplier,
                viewModel.PnfBoxPercent,
                viewModel.PnfReversal,
                2, // RenkoReversal default
                viewModel.PnfRoundingMode,
                viewModel.PnfFallbackMode),

            _ => new ChartStrategyParameters(
                ChartSizingMode.Fixed,
                0m,
                14,
                1.0m,
                0m)
        };
    }
}
