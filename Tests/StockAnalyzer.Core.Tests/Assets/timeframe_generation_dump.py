"""Runs the real generate_timeframes.resample_and_save on a daily series and prints the weekly and monthly bars it wrote.

Test helper of TimeframeGenerationParityTests: the C# test compares this output with TimeFrameAggregator. Nothing is written
outside --workdir (the module's data roots are redirected before the call).

usage: timeframe_generation_dump.py --python-dir <StockAnalyzer.Python> --bars <bars.json> --workdir <empty folder>
bars.json: [{"date": "YYYY-MM-DD", "open": n, "high": n, "low": n, "close": n, "volume": n}, ...] (oldest first)
stdout: one JSON object {"weekly": [bar, ...], "monthly": [bar, ...]} with the same bar shape.
"""
import argparse
import datetime
import json
import os
import sys

import polars as pl

SYMBOL = "PARITY"
BAR_COLUMNS = ["date", "open", "high", "low", "close", "volume"]


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--python-dir", required=True)
    parser.add_argument("--bars", required=True)
    parser.add_argument("--workdir", required=True)
    args = parser.parse_args()

    with open(args.bars, encoding="utf-8") as handle:
        rows = json.load(handle)

    # The dtypes of update_daily.py: Date, Float32 prices, Int64 volume.
    daily = pl.DataFrame({
        "date": [datetime.date.fromisoformat(r["date"]) for r in rows],
        "open": [r["open"] for r in rows],
        "high": [r["high"] for r in rows],
        "low": [r["low"] for r in rows],
        "close": [r["close"] for r in rows],
        "volume": [r["volume"] for r in rows],
    }).with_columns(
        pl.col("open").cast(pl.Float32), pl.col("high").cast(pl.Float32),
        pl.col("low").cast(pl.Float32), pl.col("close").cast(pl.Float32),
        pl.col("volume").cast(pl.Int64),
    )

    roots = {name: os.path.join(args.workdir, name) for name in ("Daily", "Weekly", "Monthly")}
    os.makedirs(roots["Daily"], exist_ok=True)
    daily.write_parquet(os.path.join(roots["Daily"], f"{SYMBOL}.parquet"))

    sys.path.insert(0, args.python_dir)
    import generate_timeframes as gt  # noqa: E402  (imported after the path is set)

    gt.DAILY_DATA_ROOT = roots["Daily"]
    gt.WEEKLY_DATA_ROOT = roots["Weekly"]
    gt.MONTHLY_DATA_ROOT = roots["Monthly"]
    gt.ensure_directories()
    if not gt.resample_and_save(SYMBOL):
        raise SystemExit("generate_timeframes reported a failure")

    result = {}
    for key, root in (("weekly", roots["Weekly"]), ("monthly", roots["Monthly"])):
        path = os.path.join(root, f"{SYMBOL}.parquet")
        if not os.path.exists(path):
            raise SystemExit(f"generate_timeframes wrote no {key} file")
        frame = pl.read_parquet(path).sort("date")
        result[key] = [
            {"date": r["date"].isoformat(), "open": r["open"], "high": r["high"], "low": r["low"], "close": r["close"], "volume": r["volume"]}
            for r in frame.select(BAR_COLUMNS).to_dicts()
        ]

    print(json.dumps(result))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
