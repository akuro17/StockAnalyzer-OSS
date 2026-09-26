import os
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

def resample_and_save(symbol):
    daily_path = os.path.join(DAILY_DATA_ROOT, f"{symbol}.parquet")
    if not os.path.exists(daily_path):
        logging.warning(f"Daily data not found for {symbol}")
        return

    try:
        df_daily = pl.read_parquet(daily_path).filter(pl.col("date").is_not_null() & pl.col("close").is_not_null())
        if df_daily.is_empty():
            return

        df_weekly = (
            df_daily
            .sort("date")
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
            .sort("date")
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

        weekly_path = os.path.join(WEEKLY_DATA_ROOT, f"{symbol}.parquet")
        monthly_path = os.path.join(MONTHLY_DATA_ROOT, f"{symbol}.parquet")
        
        weekly_tmp = f"{weekly_path}.tmp"
        monthly_tmp = f"{monthly_path}.tmp"

        df_weekly.write_parquet(weekly_tmp, compression="zstd")
        if os.path.exists(weekly_path):
            os.remove(weekly_path)
        os.replace(weekly_tmp, weekly_path)

        df_monthly.write_parquet(monthly_tmp, compression="zstd")
        if os.path.exists(monthly_path):
            os.remove(monthly_path)
        os.replace(monthly_tmp, monthly_path)
        
        logging.info(f"{symbol}: Generated Weekly ({len(df_weekly)}) and Monthly ({len(df_monthly)}) Parquet files.")

    except Exception as e:
        logging.error(f"Failed to process {symbol}: {e}")

def main():
    parser = argparse.ArgumentParser(description="Generate weekly and monthly timeframes.")
    parser.add_argument("--ticker", "-t", type=str, help="Specific ticker to update. If omitted, updates all.")
    args = parser.parse_args()

    ensure_directories()
    
    if args.ticker:
        tickers = [t.strip().upper() for t in args.ticker.split(",")]
    else:
        # Get list of tickers from Daily directory
        files = [f for f in os.listdir(DAILY_DATA_ROOT) if f.endswith(".parquet")]
        tickers = [f.replace(".parquet", "") for f in files]
    
    logging.info(f"Starting pre-generation for {len(tickers)} tickers...")
    
    for symbol in tickers:
        resample_and_save(symbol)
        
    logging.info("Pre-generation process completed.")

if __name__ == "__main__":
    main()
