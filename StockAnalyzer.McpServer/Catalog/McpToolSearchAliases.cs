using System.Collections.Frozen;

namespace StockAnalyzer.McpServer.Catalog;

/// <summary>
/// Single Source of Truth for English/ASCII domain aliases indexed by <c>sa_search_tools</c>.
/// All keys match registered MCP tool names and all alias tokens are strictly lowercase ASCII (<c>[a-z0-9_]+</c>).
/// </summary>
internal static class McpToolSearchAliases
{
    public static FrozenDictionary<string, FrozenSet<string>> ByToolName { get; } = Build();

    private static FrozenDictionary<string, FrozenSet<string>> Build()
    {
        var map = new Dictionary<string, FrozenSet<string>>(StringComparer.Ordinal)
        {
            ["sa_calculate_indicators"] = Aliases(
                "rsi", "macd", "sma", "ema", "wma", "atr", "adx", "cci", "obv", "vwap",
                "bollinger", "bb", "ichimoku", "stochastic", "roc", "momentum", "volatility",
                "oscillator", "moving_average", "keltner", "donchian", "supertrend", "psar",
                "aroon", "mfi", "cmf", "tsi", "kst", "ppo", "hma", "kama", "dema", "tema",
                "zscore", "kalman", "hurst", "entropy", "garch"),
            ["sa_calculate_indicators_batch"] = Aliases(
                "batch", "multi_symbol", "rsi", "macd", "sma", "ema", "atr", "bollinger", "scanner"),
            ["sa_detect_candle_patterns"] = Aliases(
                "candlestick", "doji", "hammer", "engulfing", "harami", "morning_star",
                "evening_star", "marubozu", "shooting_star", "price_action"),
            ["sa_detect_chart_patterns"] = Aliases(
                "head_and_shoulders", "double_top", "double_bottom", "triangle", "wedge",
                "flag", "cup_and_handle", "dtw", "harmonic"),
            ["sa_detect_divergences"] = Aliases(
                "divergence", "rsi_divergence", "macd_divergence", "hidden_divergence",
                "regular_divergence", "oscillator"),
            ["sa_run_backtest"] = Aliases(
                "sharpe", "sortino", "drawdown", "calmar", "win_rate", "profit_factor",
                "equity_curve", "cagr", "strategy", "simulation", "stop_loss", "take_profit",
                "mfe", "mae"),
            ["sa_optimize_backtest"] = Aliases(
                "grid_search", "parameter_sweep", "sharpe", "calmar", "hyperparameter",
                "tuning", "optimization"),
            ["sa_run_walk_forward_analysis"] = Aliases(
                "wfa", "overfitting", "out_of_sample", "oos", "rolling_window", "robustness"),
            ["sa_run_portfolio_backtest"] = Aliases(
                "portfolio", "rebalance", "multi_asset", "allocation", "sharpe", "drawdown"),
            ["sa_analyze_portfolio_risk"] = Aliases(
                "var", "cvar", "beta", "volatility", "sharpe", "sortino", "drawdown",
                "correlation", "risk", "value_at_risk"),
            ["sa_calculate_cross_asset_stats"] = Aliases(
                "correlation", "cointegration", "beta", "alpha", "spread", "pairs",
                "rolling_correlation", "lead_lag"),
            ["sa_calculate_drawing_values"] = Aliases(
                "fibonacci", "fib", "pitchfork", "trendline", "channel", "vwap",
                "anchored_vwap", "gann", "retracement", "regression_channel"),
            ["sa_evaluate_expressions"] = Aliases(
                "calculator", "formula", "math", "dsl", "expression", "statistics",
                "regression", "tvm", "bond", "black_scholes"),
            ["sa_find_support_resistance"] = Aliases(
                "support", "resistance", "pivot", "swing_high", "swing_low",
                "key_levels", "supply_demand"),
            ["sa_analyze_market_structure"] = Aliases(
                "bos", "choch", "mss", "swing", "trend_structure", "higher_high",
                "lower_low", "break_of_structure"),
            ["sa_calculate_volume_profile"] = Aliases(
                "poc", "vah", "val", "value_area", "tpo", "volume_at_price"),
            ["sa_calculate_market_breadth"] = Aliases(
                "advance_decline", "mcclellan", "breadth", "new_highs", "new_lows"),
            ["sa_analyze_cycles"] = Aliases(
                "fft", "hilbert", "autocorrelation", "dominant_cycle", "periodogram", "spectral"),
            ["sa_list_result_handles"] = Aliases(
                "handles", "result_handles", "result_id", "screener_results", "cache"),
        };

        return map.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private static FrozenSet<string> Aliases(params string[] values)
        => values.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
}
