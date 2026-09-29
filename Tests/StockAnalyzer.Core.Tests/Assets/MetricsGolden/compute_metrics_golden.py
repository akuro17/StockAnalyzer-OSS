"""Independent golden-value generator for the backtest report metrics (L3 verification).

Computes every metric ONLY from the formulas in the P2 spec document (Y:\\0915 Backtesting\\02_P2_ReportCalculator.md, sections 5.2-5.4),
using nothing but the Python standard library - it shares no code with StockAnalyzer. Output: metrics_golden_cases.json (committed; the C# test
StockAnalyzer.Core.Tests.Backtest.Verification.L3_MetricsGoldenTests reads it).

Run:  python compute_metrics_golden.py     (deterministic: every random draw is seeded)

Sample definition (current StockAnalyzer behavior, see L2_ReportPeriodBoundaryTests):
  E[0] = initial capital, E[1..m] = per-bar equity points from index historyStartIndex onward (the bar AT the index is included),
  r[j] = E[j] / E[j-1] - 1 evaluated in Decimal (28 digits) and then narrowed to float.
"""
import json
import math
import random
from datetime import date
from decimal import Decimal, getcontext
from pathlib import Path

getcontext().prec = 28

SEED = 20260919
DAYS_PER_YEAR = 365.2425  # spec 5.4 CAGR: Y = (End - Start) / TicksPerDay / 365.2425


def money(x: float) -> str:
    return f"{x:.2f}"


def build_walk_case(name, seed, bar_count, trade_count, annual_periods, rf, mar, history_start, start, end):
    rng = random.Random(seed)
    initial = Decimal("100000.00")
    points = []
    equity = initial
    for _ in range(bar_count):
        ret = rng.gauss(0.0004, 0.01)
        equity = Decimal(money(float(equity) * (1.0 + ret)))
        points.append(equity)
    trades = [Decimal(money(rng.gauss(150.0, 900.0))) for _ in range(trade_count)]
    return {
        "name": name,
        "initialCapital": str(initial),
        "pointEquity": [str(p) for p in points],
        "tradeClosedNet": [str(t) for t in trades],
        "annualPeriods": annual_periods,
        "annualRiskFreeRate": str(rf),
        "annualMar": str(mar),
        "historyStartIndex": history_start,
        "evaluationStart": start.isoformat(),
        "evaluationEnd": end.isoformat(),
    }


def textbook_case():
    # Spec section 6: E = [100, 120, 90, 108]  => H = [100,120,120,120], DD = [0,0,.25,.10]
    return {
        "name": "textbook",
        "initialCapital": "100",
        "pointEquity": ["120", "90", "108"],
        "tradeClosedNet": ["10", "-5", "0"],
        "annualPeriods": 252,
        "annualRiskFreeRate": "0",
        "annualMar": "0",
        "historyStartIndex": 0,
        "evaluationStart": date(2024, 1, 1).isoformat(),
        "evaluationEnd": date(2024, 12, 31).isoformat(),
    }


def compute_expected(case):
    d = Decimal
    initial = d(case["initialCapital"])
    points = [d(p) for p in case["pointEquity"]]
    start = case["historyStartIndex"]
    sample = points[start:]
    E = [initial] + sample
    m = len(E) - 1
    A = case["annualPeriods"]
    rf = float(d(case["annualRiskFreeRate"]) / d(A))
    mar = float(d(case["annualMar"]) / d(A))
    trades = [d(t) for t in case["tradeClosedNet"]]

    expected = {}
    expected["TotalPnL"] = float(E[m] - E[0])
    expected["TotalReturn"] = float(E[m] / E[0] - 1)

    y_days = (date.fromisoformat(case["evaluationEnd"]) - date.fromisoformat(case["evaluationStart"])).days
    Y = y_days / DAYS_PER_YEAR
    expected["CAGR"] = float(E[m] / E[0]) ** (1.0 / Y) - 1.0

    K = len(trades)
    wins = sum(1 for t in trades if t > 0)
    gains = sum((t for t in trades if t > 0), d(0))
    losses = sum((-t for t in trades if t < 0), d(0))
    expected["WinRate"] = wins / K
    expected["ProfitFactor"] = float(gains / losses)
    expected["ExpectedPayoff"] = float(sum(trades, d(0)) / K)

    # drawdown series over E[0..m]
    peak = E[0]
    ratios, amounts = [], []
    for e in E:
        peak = max(peak, e)
        ratios.append(float((peak - e) / peak))
        amounts.append(float(peak - e))
    expected["MaxDrawdown"] = max(ratios)
    expected["MaxDrawdownAmount"] = max(amounts)
    expected["UlcerIndex"] = math.sqrt(sum((100.0 * ratios[j]) ** 2 for j in range(1, m + 1)) / m)

    r = [float(E[j] / E[j - 1] - 1) for j in range(1, m + 1)]
    x = [v - rf for v in r]
    mean_x = sum(x) / m
    var_x = sum((v - mean_x) ** 2 for v in x) / (m - 1)
    bar_sharpe = mean_x / math.sqrt(var_x)
    expected["BarSharpe"] = bar_sharpe
    expected["AnnualizedSharpe"] = bar_sharpe * math.sqrt(A)

    y = [v - mar for v in r]
    mean_y = sum(y) / m
    downside = sum(min(v, 0.0) ** 2 for v in y) / m
    bar_sortino = mean_y / math.sqrt(downside)
    expected["BarSortino"] = bar_sortino
    expected["AnnualizedSortino"] = bar_sortino * math.sqrt(A)

    expected["CalmarFullPeriod"] = expected["CAGR"] / expected["MaxDrawdown"]
    expected["RecoveryFactor"] = expected["TotalPnL"] / expected["MaxDrawdownAmount"]
    return expected


def main():
    cases = [
        textbook_case(),
        build_walk_case("walk_h0", SEED, 120, 25, 252, "0.02", "0.03", 0, date(2024, 1, 1), date(2024, 12, 31)),
        build_walk_case("walk_h10", SEED + 1, 120, 25, 52, "-0.01", "0", 10, date(2022, 1, 3), date(2024, 6, 28)),
    ]
    for case in cases:
        case["expected"] = compute_expected(case)
    out = Path(__file__).with_name("metrics_golden_cases.json")
    out.write_text(json.dumps({"cases": cases}, indent=2) + "\n", encoding="utf-8")
    print(f"wrote {out} ({len(cases)} cases)")


if __name__ == "__main__":
    main()
