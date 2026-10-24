import os
import sys
import datetime
import logging
import polars as pl
from indicators.adapter import IndicatorAdapter
import argparse

# Setup Logging
logging.basicConfig(
    level=logging.INFO,
    format='[%(levelname)s] %(asctime)s - %(message)s',
    datefmt='%Y-%m-%d %H:%M:%S'
)

# Configuration
DAILY_DATA_ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "Data", "Daily"))
WEEKLY_DATA_ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "Data", "Weekly"))
MONTHLY_DATA_ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "Data", "Monthly"))

BAR_COLUMNS = ["date", "open", "high", "low", "close", "volume"]

def ensure_directories():
    for d in [WEEKLY_DATA_ROOT, MONTHLY_DATA_ROOT]:
        if not os.path.exists(d):
            os.makedirs(d)
            logging.info(f"Created directory: {d}")

def calculate_indicators(df):
    if df.is_empty():
        return df

    try:
        candles = df.to_dicts()
        adapter = IndicatorAdapter()

        # 1. SMA 20
        sma_values = adapter.calculate("SimpleMovingAverage", candles, {"period": 20})

        # 2. RSI 14
        rsi_values = adapter.calculate("RelativeStrengthIndex", candles, {"period": 14})

        res_df = df.with_columns([
            pl.Series("SMA_20", sma_values).cast(pl.Float32),
            pl.Series("RSI_14", rsi_values).cast(pl.Float32)
        ])

        return res_df
    except Exception as e:
        logging.error(f"Error calculating indicators: {e}")
        return df

def _present(values):
    return [v for v in values if v is not None]

def _expected_periods(rows, period):
    """Re-aggregates the daily rows (plain Python, independent of polars) into ISO weeks or calendar months.

    Returns [(label, open, high, low, close, volume)] oldest first. The label is the first CALENDAR day of the period (the Monday of the ISO
    week, the 1st of the month), which is how polars labels a dynamic window; TimeFrameAggregator (C#) labels the first TRADING day.
    """
    groups = {}
    for row in rows:
        day = row[0]
        key = tuple(day.isocalendar())[:2] if period == "week" else (day.year, day.month)
        groups.setdefault(key, []).append(row)

    result = []
    for key in sorted(groups):
        bars = groups[key]
        first_day = bars[0][0]
        label = first_day - datetime.timedelta(days=first_day.weekday()) if period == "week" else first_day.replace(day=1)
        highs = _present([b[2] for b in bars])
        lows = _present([b[3] for b in bars])
        result.append((label, bars[0][1], max(highs) if highs else None, min(lows) if lows else None, bars[-1][4], sum(b[5] or 0 for b in bars)))
    return result

def verify_symbol(symbol, daily_rows):
    """Reads the saved weekly and monthly files back and compares every bar with a re-aggregation of the daily rows.

    Returns the list of problems (empty when the files are the aggregation of the daily file).
    """
    problems = []
    for period, root in (("week", WEEKLY_DATA_ROOT), ("month", MONTHLY_DATA_ROOT)):
        path = os.path.join(root, f"{symbol}.parquet")
        if not os.path.exists(path):
            problems.append(f"{symbol}: {period} file is missing")
            continue
        saved = pl.read_parquet(path).sort("date").select(BAR_COLUMNS).rows()
        expected = _expected_periods(daily_rows, period)
        if len(saved) != len(expected):
            problems.append(f"{symbol}: {period} file has {len(saved)} bars, the daily file gives {len(expected)}")
            continue
        for index, (have, want) in enumerate(zip(saved, expected)):
            if tuple(have) != tuple(want):
                problems.append(f"{symbol}: {period} bar {index} is {tuple(have)}, the daily file gives {tuple(want)}")
                break
    return problems

def resample_and_save(symbol):
    """Writes Data/Weekly and Data/Monthly of one symbol from its daily file and verifies what was written. Returns True on success.

    Every failure is logged and returned as False (the caller turns it into the exit code), and an old file is never removed: a failed
    run leaves the previous files, which verify_symbol / the exit code then report as not current.
    """
    # Date label of a bar: the first CALENDAR day of its period (the Monday of the ISO week, the 1st of the month), which is how polars labels
    # a dynamic window. TimeFrameAggregator (C#) labels the first TRADING day; the open, high, low, close and volume are the same
    # (Tests/StockAnalyzer.Core.Tests/Services/TimeframeGenerationParityTests.cs proves it, and the label rule).
    daily_path = os.path.join(DAILY_DATA_ROOT, f"{symbol}.parquet")
    if not os.path.exists(daily_path):
        logging.error(f"Daily data not found for {symbol}")
        return False

    weekly_path = os.path.join(WEEKLY_DATA_ROOT, f"{symbol}.parquet")
    monthly_path = os.path.join(MONTHLY_DATA_ROOT, f"{symbol}.parquet")
    weekly_tmp = f"{weekly_path}.tmp"
    monthly_tmp = f"{monthly_path}.tmp"

    try:
        df_daily = pl.read_parquet(daily_path).filter(pl.col("date").is_not_null() & pl.col("close").is_not_null())
        if df_daily.is_empty():
            logging.error(f"{symbol}: the daily file holds no usable bars; the weekly and monthly files were not changed")
            return False

        df_daily = df_daily.sort("date")

        df_weekly = (
            df_daily
            .group_by_dynamic("date", every="1w")
            .agg([
                pl.col("open").first(),
                pl.col("high").max(),
                pl.col("low").min(),
                pl.col("close").last(),
                pl.col("volume").sum()
            ])
        )

        df_monthly = (
            df_daily
            .group_by_dynamic("date", every="1mo")
            .agg([
                pl.col("open").first(),
                pl.col("high").max(),
                pl.col("low").min(),
                pl.col("close").last(),
                pl.col("volume").sum()
            ])
        )

        df_weekly = calculate_indicators(df_weekly)
        df_monthly = calculate_indicators(df_monthly)

        # Both temp files are complete before either target is touched; a target is replaced in one step (no remove-then-replace gap).
        df_weekly.write_parquet(weekly_tmp, compression="zstd")
        df_monthly.write_parquet(monthly_tmp, compression="zstd")
        os.replace(weekly_tmp, weekly_path)
        os.replace(monthly_tmp, monthly_path)

        problems = verify_symbol(symbol, df_daily.select(BAR_COLUMNS).rows())
        if problems:
            for problem in problems:
                logging.error(problem)
            return False

        logging.info(f"{symbol}: Generated Weekly ({len(df_weekly)}) and Monthly ({len(df_monthly)}) Parquet files (verified against the daily file).")
        return True

    except Exception as e:
        logging.error(f"Failed to process {symbol}: {e}")
        return False
    finally:
        for tmp in (weekly_tmp, monthly_tmp):
            if os.path.exists(tmp):
                try:
                    os.remove(tmp)
                except OSError:
                    pass

def run(tickers):
    """Generates the files of every ticker; returns how many failed."""
    failed = [symbol for symbol in tickers if not resample_and_save(symbol)]
    if failed:
        logging.error(f"Weekly/monthly generation FAILED for {len(failed)} of {len(tickers)} tickers: {', '.join(failed)}")
    return len(failed)

def selfcheck():
    """Offline check of the failure and verification rules in a temporary folder (python generate_timeframes.py --selfcheck)."""
    import tempfile

    global DAILY_DATA_ROOT, WEEKLY_DATA_ROOT, MONTHLY_DATA_ROOT
    saved_roots = (DAILY_DATA_ROOT, WEEKLY_DATA_ROOT, MONTHLY_DATA_ROOT)
    with tempfile.TemporaryDirectory() as tmp:
        DAILY_DATA_ROOT, WEEKLY_DATA_ROOT, MONTHLY_DATA_ROOT = (os.path.join(tmp, name) for name in ("Daily", "Weekly", "Monthly"))
        try:
            os.makedirs(DAILY_DATA_ROOT)
            ensure_directories()

            days = []
            day = datetime.date(2024, 12, 2)
            while day <= datetime.date(2025, 2, 28):
                if day.weekday() < 5 and day not in (datetime.date(2024, 12, 25), datetime.date(2025, 1, 1)):
                    days.append(day)
                day += datetime.timedelta(days=1)
            frame = pl.DataFrame({
                "date": days,
                "open": [100.0 + i * 0.25 for i in range(len(days))],
                "high": [101.0 + i * 0.25 for i in range(len(days))],
                "low": [99.0 + i * 0.25 for i in range(len(days))],
                "close": [100.5 + i * 0.25 for i in range(len(days))],
                "volume": [1000 + i for i in range(len(days))],
            }).with_columns(pl.col("open").cast(pl.Float32), pl.col("high").cast(pl.Float32), pl.col("low").cast(pl.Float32),
                            pl.col("close").cast(pl.Float32), pl.col("volume").cast(pl.Int64))
            frame.write_parquet(os.path.join(DAILY_DATA_ROOT, "AAA.parquet"))
            pl.DataFrame(schema=frame.schema).write_parquet(os.path.join(DAILY_DATA_ROOT, "EMPTY.parquet"))

            # 1. A normal run succeeds, verifies, and leaves no temp file.
            assert run(["AAA"]) == 0
            rows = frame.select(BAR_COLUMNS).rows()
            assert verify_symbol("AAA", rows) == []
            assert not [name for root in (WEEKLY_DATA_ROOT, MONTHLY_DATA_ROOT) for name in os.listdir(root) if name.endswith(".tmp")]

            # 2. The verification sees a saved file that is not the aggregation of the daily file.
            weekly_path = os.path.join(WEEKLY_DATA_ROOT, "AAA.parquet")
            good = pl.read_parquet(weekly_path)
            good.with_columns((pl.col("close") + 1).cast(pl.Float32)).write_parquet(weekly_path)
            assert verify_symbol("AAA", rows), "a changed close must be reported"
            good.head(good.height - 1).write_parquet(weekly_path)
            assert verify_symbol("AAA", rows), "a missing last bar must be reported"
            os.remove(weekly_path)
            assert any("missing" in p for p in verify_symbol("AAA", rows)), "a missing file must be reported"

            # 3. Each kind of failure is counted (the exit code), and an old file is not removed by a failed run.
            stale = os.path.join(MONTHLY_DATA_ROOT, "EMPTY.parquet")
            good.write_parquet(stale)
            assert run(["EMPTY"]) == 1 and os.path.exists(stale), "an empty daily file fails and keeps the old file"
            assert run(["NONE"]) == 1, "a missing daily file fails"
            assert run(["AAA", "NONE", "EMPTY"]) == 2, "one good symbol does not hide the failures"
            assert verify_symbol("AAA", rows) == [], "the good symbol was still generated"
        finally:
            DAILY_DATA_ROOT, WEEKLY_DATA_ROOT, MONTHLY_DATA_ROOT = saved_roots
    print("SELFCHECK OK")

def main():
    parser = argparse.ArgumentParser(description="Generate weekly and monthly timeframes.")
    parser.add_argument("--ticker", "-t", type=str, help="Specific ticker to update. If omitted, updates all.")
    parser.add_argument("--selfcheck", action="store_true", help="Run the offline check of the failure and verification rules and exit.")
    args = parser.parse_args()

    if args.selfcheck:
        selfcheck()
        return

    ensure_directories()

    if args.ticker:
        tickers = [t.strip().upper() for t in args.ticker.split(",")]
    else:
        # Get list of tickers from Daily directory
        files = [f for f in os.listdir(DAILY_DATA_ROOT) if f.endswith(".parquet")]
        tickers = [f.replace(".parquet", "") for f in files]

    logging.info(f"Starting pre-generation for {len(tickers)} tickers...")

    failures = run(tickers)

    if failures:
        logging.error("Pre-generation process completed WITH FAILURES.")
        sys.exit(1)

    logging.info("Pre-generation process completed.")

if __name__ == "__main__":
    main()
