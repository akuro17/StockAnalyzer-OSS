import os
import logging
import argparse
import polars as pl
from data_provider import StockDataProvider

# Setup Logging
logging.basicConfig(
    level=logging.INFO,
    format='[%(levelname)s] %(asctime)s - %(message)s',
    datefmt='%Y-%m-%d %H:%M:%S'
)
logger = logging.getLogger(__name__)

# Configuration
DEFAULT_METADATA_ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "Data", "Metadata"))

def to_float(val):
    if val is None or val == "" or val == "N/A" or val == "None":
        return None
    try:
        return float(val)
    except (ValueError, TypeError):
        return None

def to_int(val):
    if val is None or val == "" or val == "N/A" or val == "None":
        return None
    try:
        return int(val)
    except (ValueError, TypeError):
        return None

def update_ticker_metadata(symbol, metadata_root=DEFAULT_METADATA_ROOT):
    """Fetch metadata for a ticker and save it as a parquet file."""
    os.makedirs(metadata_root, exist_ok=True)
    
    logger.info(f"Fetching metadata for {symbol}...")
    provider = StockDataProvider()
    metadata = provider.get_ticker_metadata(symbol)
    
    if metadata.get("status") == "ok":
        # Prepare data for Parquet (Strict schema compliance with C# ParquetMarketDataProvider)
        data = {
            "short_name": [metadata.get("shortName", "")],
            "long_name": [metadata.get("longName", "")],
            "region": [metadata.get("country", "")],
            "sector": [metadata.get("sector", "")],
            "industry": [metadata.get("industry", "")],
            "currency": [metadata.get("currency", "")],
            "current_price": [to_float(metadata.get("currentPrice"))],
            "last_close": [to_float(metadata.get("lastClose"))],
            
            # Fundamentals
            "return_on_equity": [to_float(metadata.get("returnOnEquity"))],
            "return_on_assets": [to_float(metadata.get("returnOnAssets"))],
            "gross_margins": [to_float(metadata.get("grossMargins"))],
            "operating_margins": [to_float(metadata.get("operatingMargins"))],
            "profit_margins": [to_float(metadata.get("profitMargins"))],
            "current_ratio": [to_float(metadata.get("currentRatio"))],
            "debt_to_equity": [to_float(metadata.get("debtToEquity"))],
            "ebitda": [to_float(metadata.get("ebitda"))],
            "free_cashflow": [to_float(metadata.get("freeCashflow"))],
            "operating_cashflow": [to_float(metadata.get("operatingCashflow"))],
            "trailing_pe": [to_float(metadata.get("trailingPE"))],
            "forward_pe": [to_float(metadata.get("forwardPE"))],
            "price_to_book": [to_float(metadata.get("priceToBook"))],
            "trailing_eps": [to_float(metadata.get("trailingEps"))],
            "forward_eps": [to_float(metadata.get("forwardEps"))],
            "book_value": [to_float(metadata.get("bookValue"))],
            "shares_outstanding": [to_float(metadata.get("sharesOutstanding"))],
            "float_shares": [to_float(metadata.get("floatShares"))],
            "short_ratio": [to_float(metadata.get("shortRatio"))],
            "short_percent_of_float": [to_float(metadata.get("shortPercentOfFloat"))],
            "held_percent_insiders": [to_float(metadata.get("heldPercentInsiders"))],
            "held_percent_institutions": [to_float(metadata.get("heldPercentInstitutions"))],
            "long_business_summary": [metadata.get("longBusinessSummary", None)],
            "full_time_employees": [to_int(metadata.get("fullTimeEmployees"))],
            "fifty_two_week_high": [to_float(metadata.get("fiftyTwoWeekHigh"))],
            "fifty_two_week_low": [to_float(metadata.get("fiftyTwoWeekLow"))],
            "revenue_growth": [to_float(metadata.get("revenueGrowth"))],
            "earnings_growth": [to_float(metadata.get("earningsGrowth"))],
            "enterprise_value": [to_float(metadata.get("enterpriseValue"))],
            "enterprise_to_ebitda": [to_float(metadata.get("enterpriseToEbitda"))],
            "beta": [to_float(metadata.get("beta"))],
            "payout_ratio": [to_float(metadata.get("payoutRatio"))],
            "dividend_rate": [to_float(metadata.get("dividendRate"))],
            "dividend_yield": [to_float(metadata.get("dividendYield"))],
            "total_debt": [to_float(metadata.get("totalDebt"))],
            "total_cash": [to_float(metadata.get("totalCash"))],
            "total_revenue": [to_float(metadata.get("totalRevenue"))],
            "market_cap": [to_float(metadata.get("marketCap"))],
            
            # Additional raw fields
            "price_to_sales_trailing_12_months": [to_float(metadata.get("priceToSalesTrailing12Months"))],
            "enterprise_to_revenue": [to_float(metadata.get("enterpriseToRevenue"))],
            "ebitda_margins": [to_float(metadata.get("ebitdaMargins"))],
            "quick_ratio": [to_float(metadata.get("quickRatio"))],
            "average_volume": [to_float(metadata.get("averageVolume"))],

            # Derived (Python writes null, C# calculates and updates dynamically on load or sync)
            "pbr_calculated": [None],
            "dividend_yield_calculated": [None],
            "earnings_yield": [None],
            "fcf_yield": [None],
            "fcf_margin": [None],
            "net_debt": [None],
            "net_debt_to_ebitda": [None],
            "dividend_coverage": [None],
            "pct_from_fifty_two_week_high": [None],
            "float_ratio": [None],
            "market_cap_per_employee": [None],
            "peg_ratio": [to_float(metadata.get("pegRatio"))],
            "operating_cash_flow_yield": [None],
            "net_cash_ratio": [None],
            "price_to_cash_flow_ratio": [None],
            "net_debt_equity_ratio": [None],
            "fifty_two_week_range_position": [None],
            "daily_turnover_rate": [None],
            "average_turnover_rate": [None],
            "daily_float_turnover_ratio": [None],
            "average_float_turnover": [None],
            
            # New yfinance metadata fields
            "quote_type": [metadata.get("quoteType", "N/A")],
            "exchange_timezone_name": [metadata.get("exchangeTimezoneName", "N/A")],
            "gmt_offset_milliseconds": [to_int(metadata.get("gmtOffSetMilliseconds"))],
            "ex_dividend_date": [to_int(metadata.get("exDividendDate"))],
            "last_fiscal_year_end": [to_int(metadata.get("lastFiscalYearEnd"))],
            "most_recent_quarter": [to_int(metadata.get("mostRecentQuarter"))],
            "target_high_price": [to_float(metadata.get("targetHighPrice"))],
            "target_low_price": [to_float(metadata.get("targetLowPrice"))],
            "target_mean_price": [to_float(metadata.get("targetMeanPrice"))],
            "target_median_price": [to_float(metadata.get("targetMedianPrice"))],
            "recommendation_key": [metadata.get("recommendationKey", "N/A")],
            "recommendation_mean": [to_float(metadata.get("recommendationMean"))],
            "number_of_analyst_opinions": [to_int(metadata.get("numberOfAnalystOpinions"))],

            # Timestamp (will be populated on read/write in C#, Python writes None)
            "metadata_last_updated": [None]
        }
        
        # Define explicit schema to prevent Polars from inferring types as Null or mismatched types when fields are None
        schema = {
            "short_name": pl.String,
            "long_name": pl.String,
            "region": pl.String,
            "sector": pl.String,
            "industry": pl.String,
            "currency": pl.String,
            "current_price": pl.Float64,
            "last_close": pl.Float64,
            
            # Fundamentals
            "return_on_equity": pl.Float64,
            "return_on_assets": pl.Float64,
            "gross_margins": pl.Float64,
            "operating_margins": pl.Float64,
            "profit_margins": pl.Float64,
            "current_ratio": pl.Float64,
            "debt_to_equity": pl.Float64,
            "ebitda": pl.Float64,
            "free_cashflow": pl.Float64,
            "operating_cashflow": pl.Float64,
            "trailing_pe": pl.Float64,
            "forward_pe": pl.Float64,
            "price_to_book": pl.Float64,
            "trailing_eps": pl.Float64,
            "forward_eps": pl.Float64,
            "book_value": pl.Float64,
            "shares_outstanding": pl.Float64,
            "float_shares": pl.Float64,
            "short_ratio": pl.Float64,
            "short_percent_of_float": pl.Float64,
            "held_percent_insiders": pl.Float64,
            "held_percent_institutions": pl.Float64,
            "long_business_summary": pl.String,
            "full_time_employees": pl.Int64,
            "fifty_two_week_high": pl.Float64,
            "fifty_two_week_low": pl.Float64,
            "revenue_growth": pl.Float64,
            "earnings_growth": pl.Float64,
            "enterprise_value": pl.Float64,
            "enterprise_to_ebitda": pl.Float64,
            "beta": pl.Float64,
            "payout_ratio": pl.Float64,
            "dividend_rate": pl.Float64,
            "dividend_yield": pl.Float64,
            "total_debt": pl.Float64,
            "total_cash": pl.Float64,
            "total_revenue": pl.Float64,
            "market_cap": pl.Float64,
            
            # Additional raw fields
            "price_to_sales_trailing_12_months": pl.Float64,
            "enterprise_to_revenue": pl.Float64,
            "ebitda_margins": pl.Float64,
            "quick_ratio": pl.Float64,
            "average_volume": pl.Float64,

            # Derived
            "pbr_calculated": pl.Float64,
            "dividend_yield_calculated": pl.Float64,
            "earnings_yield": pl.Float64,
            "fcf_yield": pl.Float64,
            "fcf_margin": pl.Float64,
            "net_debt": pl.Float64,
            "net_debt_to_ebitda": pl.Float64,
            "dividend_coverage": pl.Float64,
            "pct_from_fifty_two_week_high": pl.Float64,
            "float_ratio": pl.Float64,
            "market_cap_per_employee": pl.Float64,
            "peg_ratio": pl.Float64,
            "operating_cash_flow_yield": pl.Float64,
            "net_cash_ratio": pl.Float64,
            "price_to_cash_flow_ratio": pl.Float64,
            "net_debt_equity_ratio": pl.Float64,
            "fifty_two_week_range_position": pl.Float64,
            "daily_turnover_rate": pl.Int32,
            "average_turnover_rate": pl.Int32,
            "daily_float_turnover_ratio": pl.Int32,
            "average_float_turnover": pl.Int32,
            
            # New yfinance metadata fields
            "quote_type": pl.String,
            "exchange_timezone_name": pl.String,
            "gmt_offset_milliseconds": pl.Int64,
            "ex_dividend_date": pl.Int64,
            "last_fiscal_year_end": pl.Int64,
            "most_recent_quarter": pl.Int64,
            "target_high_price": pl.Float64,
            "target_low_price": pl.Float64,
            "target_mean_price": pl.Float64,
            "target_median_price": pl.Float64,
            "recommendation_key": pl.String,
            "recommendation_mean": pl.Float64,
            "number_of_analyst_opinions": pl.Int64,

            # Timestamp
            "metadata_last_updated": pl.Int32
        }
        
        # Preserve user-defined custom 'tag' column if existing in target metadata file
        output_path = os.path.join(metadata_root, f"{symbol}.meta.parquet")
        existing_tag = None
        if os.path.exists(output_path):
            try:
                existing_df = pl.read_parquet(output_path)
                if "tag" in existing_df.columns and len(existing_df) > 0:
                    val = existing_df["tag"][0]
                    if val is not None and str(val).strip():
                        existing_tag = str(val).strip()
            except Exception as e:
                logger.warning(f"Could not read existing tag from {output_path}: {e}")

        data["tag"] = [existing_tag]
        schema["tag"] = pl.String

        df = pl.DataFrame(data, schema=schema)
        
        # Save as .meta.parquet (Atomic Safe Write)
        tmp_path = f"{output_path}.tmp"
        
        df.write_parquet(tmp_path)
        
        if os.path.exists(output_path):
            os.remove(output_path)
        os.replace(tmp_path, output_path)
        
        logger.info(f"Saved metadata for {symbol} to {output_path}")
        return True
    else:
        logger.error(f"Failed to fetch metadata for {symbol}: {metadata.get('message', 'Unknown error')}")
        return False

def main():
    parser = argparse.ArgumentParser(description="Update ticker metadata.")
    parser.add_argument("--ticker", "-t", type=str, required=True, help="Specific ticker to update.")
    args = parser.parse_args()

    success = update_ticker_metadata(args.ticker)
    if not success:
        import sys
        sys.exit(1)

if __name__ == "__main__":
    main()
