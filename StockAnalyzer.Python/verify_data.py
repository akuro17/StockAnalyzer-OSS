import os
import polars as pl
import argparse

def verify_parquet(file_path):
    print(f"Verifying: {file_path}")
    if not os.path.exists(file_path):
        print("Error: File not found.")
        return False
    
    try:
        # Read metadata for compression info
        df = pl.scan_parquet(file_path)
        schema = df.schema
        print(f"Schema: {schema}")
        
        # Check specific types
        expected_types = {
            "date": pl.Date,
            "open": pl.Float32,
            "high": pl.Float32,
            "low": pl.Float32,
            "close": pl.Float32,
            "volume": pl.Int64
        }
        
        for col, expected in expected_types.items():
            actual = schema.get(col)
            if actual != expected:
                print(f"Warning: Column '{col}' type mismatch. Expected {expected}, got {actual}")
            else:
                print(f"OK: {col} is {actual}")

        # Check data range
        df_real = df.collect()
        print(f"Row count: {len(df_real)}")
        print(f"Date range: {df_real['date'].min()} to {df_real['date'].max()}")
        
        return True
    except Exception as e:
        print(f"Error during verification: {e}")
        return False

if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("ticker", help="Ticker symbol to verify")
    args = parser.parse_args()
    
    data_path = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "Data", "Daily", f"{args.ticker}.parquet"))
    verify_parquet(data_path)
