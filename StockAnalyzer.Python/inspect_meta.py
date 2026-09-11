import pandas as pd
import sys
import os

script_dir = os.path.dirname(os.path.abspath(__file__))
path = os.path.abspath(os.path.join(script_dir, "..", "Data", "Metadata", "AAPL.meta.parquet"))
try:
    df = pd.read_parquet(path)
    print(df.to_string())
except Exception as e:
    print(f"Error reading {path}: {e}")
