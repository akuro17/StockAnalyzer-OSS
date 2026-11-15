import os
import logging
import datetime
import polars as pl
from data_provider import (
    StockDataProvider, CORPORATE_ACTION_COLUMNS, DAILY_COLUMNS, PRICE_COLUMNS, STOCK_SPLITS_COLUMN, WHOLE_HISTORY_START_TS,
)
from symbol_validator import is_well_formed_ticker
import argparse
import json

# Setup Logging
logging.basicConfig(
    level=logging.INFO,
    format='[%(levelname)s] %(asctime)s - %(message)s',
    datefmt='%Y-%m-%d %H:%M:%S'
)

# Configuration
DEFAULT_DATA_ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "Data", "Daily"))
DATA_ROOT = os.environ.get("SA_DATA_ROOT", DEFAULT_DATA_ROOT)
CONFIG_PATH = os.path.join(os.path.dirname(__file__), "tickers.json")

def load_tickers():
    if not os.path.exists(CONFIG_PATH):
        return ["AAPL", "MSFT", "GOOGL", "AMZN", "NVDA"]
    with open(CONFIG_PATH, 'r', encoding='utf-8-sig') as f:
        return json.load(f)

def ensure_directories():
    if not os.path.exists(DATA_ROOT):
        os.makedirs(DATA_ROOT)
        logging.info(f"Created data directory: {DATA_ROOT}")

def conform_corporate_action_columns(frame):
    """Give a daily frame the three corporate-action columns as Float64.

    A column the frame lacks (a file stored before these columns existed) is added as null: null means "not fetched",
    0.0 means "fetched, no event". An existing column is only cast. Price and volume columns are never touched.
    """
    return frame.with_columns([
        pl.col(c).cast(pl.Float64) if c in frame.columns else pl.lit(None).cast(pl.Float64).alias(c)
        for c in CORPORATE_ACTION_COLUMNS
    ])


def normalize_downloaded_frame(df_pd):
    """Provider pandas frame -> storage frame: null dates/closes dropped, price columns Float32, volume Int64,
    corporate-action columns Float64. The one definition shared by the incremental and the split re-sync fetch."""
    frame = pl.from_pandas(df_pd)

    # Filter out null dates or close prices
    frame = frame.filter(pl.col("date").is_not_null() & pl.col("close").is_not_null())

    frame = frame.with_columns([
        pl.col("date").cast(pl.Date),
        pl.col("open").fill_null(pl.col("close")).cast(pl.Float32),
        pl.col("high").fill_null(pl.col("close")).cast(pl.Float32),
        pl.col("low").fill_null(pl.col("close")).cast(pl.Float32),
        pl.col("close").cast(pl.Float32),
        pl.col("volume").fill_null(0).fill_nan(0).cast(pl.Int64)
    ])
    return conform_corporate_action_columns(frame)


def needs_split_resync(existing_df, df_new):
    """True when the fetched rows hold a split (stock_splits > 0) that the stored file has not recorded.

    "Recorded" = the stored file has a row on that date whose stock_splits equals the fetched value. A stored file without
    the column (or with null there) has recorded nothing. Without a stored file there is nothing to re-sync.
    """
    if existing_df is None or existing_df.is_empty() or STOCK_SPLITS_COLUMN not in df_new.columns:
        return False
    events = df_new.filter(pl.col(STOCK_SPLITS_COLUMN) > 0).select("date", STOCK_SPLITS_COLUMN)
    if events.is_empty():
        return False
    if STOCK_SPLITS_COLUMN not in existing_df.columns:
        return True
    recorded = existing_df.select("date", STOCK_SPLITS_COLUMN)
    unrecorded = events.join(recorded, on="date", how="left", suffix="_stored").filter(
        pl.col(f"{STOCK_SPLITS_COLUMN}_stored").is_null()
        | (pl.col(f"{STOCK_SPLITS_COLUMN}_stored") != pl.col(STOCK_SPLITS_COLUMN))
    )
    return not unrecorded.is_empty()


def fetch_split_resync_frame(provider, symbol, existing_df, delay=None):
    """Whole history cut at the OLDEST stored date, or None (a warning is logged) when the stored file must stay as it is.

    The whole history is requested (not "from the oldest stored date"): a stored file that starts before 1970 would need a
    negative timestamp, which the provider cannot convert on Windows. The fetched span is cut at the oldest stored date so the
    rewritten file covers the same span as before.
    """
    oldest = existing_df.select(pl.col("date").min()).item()
    try:
        df_pd = provider.get_daily_data(symbol, start_ts=WHOLE_HISTORY_START_TS, delay=delay)
    except Exception as e:
        logging.warning(f"{symbol}: Split resync failed ({e}); stored file left unchanged")
        return None
    if df_pd is None or df_pd.empty:
        logging.warning(f"{symbol}: Split resync failed (no data returned); stored file left unchanged")
        return None
    frame = normalize_downloaded_frame(df_pd).filter(pl.col("date") >= oldest).unique(subset=["date"], keep="last").sort("date")
    if frame.is_empty() or frame.select(pl.col("date").min()).item() > oldest:
        logging.warning(f"{symbol}: Split resync skipped: provider history starts later than stored history")
        return None
    logging.info(f"{symbol}: Split resync fetched {frame.height} rows from {frame.select(pl.col('date').min()).item()} "
                 f"(stored: {existing_df.height} rows from {oldest})")
    return frame


# Safety bound of the split re-sync (an input contract the code guarantees, not a tunable: no setting may widen it).
# A re-sync replaces the whole stored span, so a provider answer that lacks stored days would silently delete history.
# The re-sync is refused when more stored days are missing from the fetched history than this share of the stored days
# (owner decision D-08-12 = B, 2026-11-15; measured 2026-11-15: 0 missing days in all 26 stored files).
# Integer percent and integer division only: a floating-point share (100 * 0.01) can round to a wrong whole number.
RESYNC_MAX_MISSING_PERCENT = 1
PERCENT_DENOMINATOR = 100
# How many of the missing days a warning names (the line stays short however many days are missing).
RESYNC_LOG_MISSING_EXAMPLES = 3


def as_calendar_days(dates):
    """Unique, ascending calendar days (pl.Date) of a date or datetime Series.

    A datetime is reduced with `.dt.date()`: the time of day is dropped, and a time-zone-aware value keeps the day of its OWN zone
    (`cast(pl.Date)` would give the UTC day, so Tokyo midnight would become the previous day). A date Series is used as it is.
    """
    days = dates if dates.dtype == pl.Date else dates.dt.date()
    return days.drop_nulls().unique().sort()


def missing_stored_days(stored_dates, fetched_dates):
    """Calendar days present in the stored history and absent from the fetched history, ascending (a pl.Series of dates)."""
    stored = as_calendar_days(stored_dates).to_frame("date")
    fetched = as_calendar_days(fetched_dates).to_frame("date")
    # A join does not promise an output order; the ascending order is part of this function's contract (first / min / max rely on it).
    return stored.join(fetched, on="date", how="anti").get_column("date").sort()


def allowed_missing_days(stored_days):
    """Missing stored days a re-sync may still replace: the share RESYNC_MAX_MISSING_PERCENT of the stored days, rounded down."""
    return stored_days * RESYNC_MAX_MISSING_PERCENT // PERCENT_DENOMINATOR


def describe_missing_days(missing, stored_count, allowed):
    """One-line problem text: counts, the span of the missing days and the first few of them."""
    first = ", ".join(day.isoformat() for day in missing.head(RESYNC_LOG_MISSING_EXAMPLES).to_list())
    return (f"stored days missing from the fetched history: {missing.len()} of {stored_count} (allowed {allowed}); "
            f"missing {missing.min().isoformat()}..{missing.max().isoformat()}, first {first}")


def validate_resync_file(path, stored_dates):
    """Read the written temp file back; return the list of problems (empty = safe to swap in).

    stored_dates: the dates of the stored file that the written file is about to replace.
    """
    frame = pl.read_parquet(path)
    problems = []
    if frame.columns != list(DAILY_COLUMNS):
        return [f"columns are {frame.columns}"]
    if any(frame.schema[c] != pl.Float32 for c in PRICE_COLUMNS):
        problems.append("price columns are not Float32")
    if frame.schema["volume"] != pl.Int64:
        problems.append("volume is not Int64")
    if any(frame.schema[c] != pl.Float64 for c in CORPORATE_ACTION_COLUMNS):
        problems.append("corporate-action columns are not Float64")
    if frame.is_empty():
        return problems + ["no rows"]
    dates = frame["date"]
    if dates.null_count() or frame["close"].null_count():
        problems.append("null date or close")
    if not dates.is_sorted() or dates.n_unique() != frame.height:
        problems.append("dates are not strictly increasing")
    stored_days = as_calendar_days(stored_dates)
    if dates.min() > stored_days.min():
        problems.append("history starts later than the stored file")
    missing = missing_stored_days(stored_days, dates)
    allowed = allowed_missing_days(stored_days.len())
    if missing.len() > allowed:
        problems.append(describe_missing_days(missing, stored_days.len(), allowed))
    return problems


def update_ticker(provider, symbol, delay=None, start_period=5, full_history=False, force_period=False):
    if not is_well_formed_ticker(symbol):
        logging.error(f"Skipping invalid ticker symbol: {symbol!r}")
        return
    file_path = os.path.join(DATA_ROOT, f"{symbol}.parquet")
    
    start_ts = WHOLE_HISTORY_START_TS
    existing_df = None
    
    if os.path.exists(file_path):
        try:
            existing_df = pl.read_parquet(file_path)
            if not existing_df.is_empty() and not force_period:
                last_date = existing_df.select(pl.col("date")).max().item()
                next_date = last_date + datetime.timedelta(days=1)
                start_ts = int(datetime.datetime.combine(next_date, datetime.time.min, tzinfo=datetime.timezone.utc).timestamp())
                logging.info(f"{symbol}: Found existing data. Last date: {last_date}. Resuming from {start_ts} ({next_date})")
        except Exception as e:
            logging.error(f"{symbol}: Error reading existing parquet: {e}")
    
    if (start_ts == WHOLE_HISTORY_START_TS or force_period) and not full_history:
        now = datetime.datetime.now(datetime.timezone.utc)
        start_year = now.year - start_period
        start_date = datetime.datetime(start_year, 1, 1, tzinfo=datetime.timezone.utc)
        start_ts = int(start_date.timestamp())
        logging.info(f"{symbol}: {'Forced period re-download' if force_period else 'No existing data'}. Downloading with lookback period of {start_period} years starting Jan 1st ({start_date.strftime('%Y-%m-%d')})")

    df_new_pd = provider.get_daily_data(symbol, start_ts=start_ts, delay=delay)
    
    if df_new_pd is None or df_new_pd.empty:
        if existing_df is None:
            raise ValueError(f"Failed to download initial data for new ticker {symbol} (data is empty or failed)")
        logging.info(f"{symbol}: No new data found.")
        return

    df_new = normalize_downloaded_frame(df_new_pd)

    resync_stored_dates = None  # set only for a split re-sync (the dates it replaces): the extra checks before the swap apply to it alone
    if existing_df is not None:
        # Sanitize existing_df just in case
        existing_df = existing_df.filter(pl.col("date").is_not_null() & pl.col("close").is_not_null())
        # A file stored before the corporate-action columns existed gets them as null before any concat.
        existing_df = conform_corporate_action_columns(existing_df)
        if needs_split_resync(existing_df, df_new):
            # A split the file has not recorded: rows stored before it are on the old scale, so replace the whole stored span.
            df_combined = fetch_split_resync_frame(provider, symbol, existing_df, delay)
            if df_combined is None:
                return
            resync_stored_dates = existing_df["date"]
            action = "Re-synced (Split)"
        elif force_period and not full_history:
            now = datetime.datetime.now(datetime.timezone.utc)
            start_year = now.year - start_period
            cutoff_date = datetime.date(start_year, 1, 1)
            existing_df_before = existing_df.filter(pl.col("date") < cutoff_date)
            df_combined = pl.concat([existing_df_before, df_new]).unique(subset=["date"], keep="last").sort("date")
            action = "Re-synced (Force Period)"
        else:
            df_combined = pl.concat([existing_df, df_new]).unique(subset=["date"], keep="last").sort("date")
            action = "Updated"
    else:
        df_combined = df_new.sort("date")
        action = "Created"

    df_combined = df_combined.filter(pl.col("date").is_not_null() & pl.col("close").is_not_null())

    try:
        tmp_path = f"{file_path}.tmp"
        df_combined.write_parquet(tmp_path, compression="zstd")
        if resync_stored_dates is not None:
            problems = validate_resync_file(tmp_path, resync_stored_dates)
            if problems:
                logging.warning(f"{symbol}: Split resync skipped: written file failed validation ({'; '.join(problems)}); stored file left unchanged")
                os.remove(tmp_path)
                return
        try:
            os.replace(tmp_path, file_path)
        except PermissionError as e:
            if resync_stored_dates is None:
                raise
            # The stored file is open elsewhere. No retry: the split stays unrecorded, so the next update detects it again.
            logging.warning(f"{symbol}: Split resync skipped: stored file is in use ({e}); stored file left unchanged")
            os.remove(tmp_path)
            return
        logging.info(f"{symbol}: {action} successfully (Atomic). Total rows: {len(df_combined)}")

        # Update 52-week high/low in metadata parquet file upon time-series update
        meta_dir = os.path.abspath(os.path.join(DATA_ROOT, "..", "Metadata"))
        meta_path = os.path.join(meta_dir, f"{symbol}.meta.parquet")
        if os.path.exists(meta_path):
            try:
                meta_df = pl.read_parquet(meta_path)
                if not meta_df.is_empty() and "date" in df_combined.columns:
                    max_date = df_combined.select(pl.col("date")).max().item()
                    cutoff = max_date - datetime.timedelta(days=365)
                    df_year = df_combined.filter(pl.col("date") >= cutoff)
                    if not df_year.is_empty():
                        h = float(df_year.select(pl.col("high")).max().item())
                        l = float(df_year.select(pl.col("low")).min().item())
                        meta_df = meta_df.with_columns([
                            pl.lit(h).cast(pl.Float64).alias("fifty_two_week_high"),
                            pl.lit(l).cast(pl.Float64).alias("fifty_two_week_low")
                        ])
                        tmp_meta = f"{meta_path}.tmp"
                        try:
                            meta_df.write_parquet(tmp_meta)
                            os.replace(tmp_meta, meta_path)
                            logging.info(f"{symbol}: Updated 52-week high/low ({h}/{l}) in metadata parquet.")
                        finally:
                            if os.path.exists(tmp_meta):
                                os.remove(tmp_meta)
            except Exception as e:
                logging.warning(f"{symbol}: Could not update 52-week high/low in metadata parquet: {e}")
    except Exception as e:
        logging.error(f"{symbol}: Failed to write parquet: {e}")
        if os.path.exists(tmp_path):
            os.remove(tmp_path)
        raise

def main():
    parser = argparse.ArgumentParser(description="Update daily market data.")
    parser.add_argument("--ticker", "-t", type=str, help="Specific ticker to update. If omitted, updates all.")
    parser.add_argument("--delay", type=float, default=None, help="Delay seconds between yfinance daily data requests.")
    parser.add_argument("--start-period", type=int, default=5, help="Lookback period in years if no existing data and full history is false.")
    parser.add_argument("--full-history", action="store_true", help="Download max period if no existing data.")
    parser.add_argument("--force-period", action="store_true", help="Force download anew within lookback period range without incremental sync.")
    args = parser.parse_args()

    ensure_directories()
    provider = StockDataProvider()
    
    tickers = load_tickers()
    if args.ticker:
        # Support multiple comma separated tickers just in case, or just one
        target_tickers = [t.strip().upper() for t in args.ticker.split(",")]
        # Filter: only update if it's in our known list, or allow any if requested
        tickers = target_tickers

    tickers = [t for t in tickers if is_well_formed_ticker(t)]

    logging.info(f"Starting daily update for {len(tickers)} tickers...")
    
    has_failed = False
    for symbol in tickers:
        try:
            update_ticker(provider, symbol, delay=args.delay, start_period=args.start_period, full_history=args.full_history, force_period=args.force_period)
        except Exception as e:
            logging.error(f"Failed to update {symbol}: {e}")
            has_failed = True
    
    logging.info("Daily update process completed.")
    if has_failed:
        import sys
        sys.exit(1)

if __name__ == "__main__":
    main()
