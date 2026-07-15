import os
import logging
import datetime
import polars as pl
from data_provider import StockDataProvider
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

def update_ticker(provider, symbol, delay=None, start_period=5, full_history=False):
    file_path = os.path.join(DATA_ROOT, f"{symbol}.parquet")
    
    start_ts = 0
    existing_df = None
    
    if os.path.exists(file_path):
        try:
            existing_df = pl.read_parquet(file_path)
            if not existing_df.is_empty():
                last_date = existing_df.select(pl.col("date")).max().item()
                next_date = last_date + datetime.timedelta(days=1)
                start_ts = int(datetime.datetime.combine(next_date, datetime.time.min).timestamp())
                logging.info(f"{symbol}: Found existing data. Last date: {last_date}. Resuming from {start_ts} ({next_date})")
        except Exception as e:
            logging.error(f"{symbol}: Error reading existing parquet: {e}")
    
    if start_ts == 0 and not full_history:
        start_date = datetime.datetime.now() - datetime.timedelta(days=365 * start_period)
        start_ts = int(start_date.timestamp())
        logging.info(f"{symbol}: No existing data. Downloading with lookback period of {start_period} years ({start_date.strftime('%Y-%m-%d')})")

    df_new_pd = provider.get_daily_data(symbol, start_ts=start_ts, delay=delay)
    
    if df_new_pd is None or df_new_pd.empty:
        if existing_df is None:
            raise ValueError(f"Failed to download initial data for new ticker {symbol} (data is empty or failed)")
        logging.info(f"{symbol}: No new data found.")
        return

    df_new = pl.from_pandas(df_new_pd)
    
    df_new = df_new.with_columns([
        pl.col("date").cast(pl.Date),
        pl.col("open").cast(pl.Float32),
        pl.col("high").cast(pl.Float32),
        pl.col("low").cast(pl.Float32),
        pl.col("close").cast(pl.Float32),
        pl.col("volume").cast(pl.Int64)
    ])

    if existing_df is not None:
        df_combined = pl.concat([existing_df, df_new]).unique(subset=["date"], keep="last").sort("date")
        action = "Updated"
    else:
        df_combined = df_new.sort("date")
        action = "Created"

    try:
        tmp_path = f"{file_path}.tmp"
        df_combined.write_parquet(tmp_path, compression="zstd")
        os.replace(tmp_path, file_path)
        logging.info(f"{symbol}: {action} successfully (Atomic). Total rows: {len(df_combined)}")
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
    args = parser.parse_args()

    ensure_directories()
    provider = StockDataProvider()
    
    tickers = load_tickers()
    if args.ticker:
        # Support multiple comma separated tickers just in case, or just one
        target_tickers = [t.strip().upper() for t in args.ticker.split(",")]
        # Filter: only update if it's in our known list, or allow any if requested
        tickers = target_tickers

    logging.info(f"Starting daily update for {len(tickers)} tickers...")
    
    has_failed = False
    for symbol in tickers:
        try:
            update_ticker(provider, symbol, delay=args.delay, start_period=args.start_period, full_history=args.full_history)
        except Exception as e:
            logging.error(f"Failed to update {symbol}: {e}")
            has_failed = True
    
    logging.info("Daily update process completed.")
    if has_failed:
        import sys
        sys.exit(1)

if __name__ == "__main__":
    main()
