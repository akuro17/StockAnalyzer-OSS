import os
import sys
import math
import datetime
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
        f = float(val)
        return None if math.isnan(f) or math.isinf(f) else f
    except (ValueError, TypeError):
        return None

def to_int(val):
    if val is None or val == "" or val == "N/A" or val == "None":
        return None
    try:
        return int(val)
    except (ValueError, TypeError):
        return None

def impute_missing_metadata(meta: dict) -> dict:
    """
    Impute and estimate missing financial metadata metrics from existing available metrics.
    Runs up to 3 passes to iteratively resolve chained metric dependencies.
    """
    if not isinstance(meta, dict):
        return meta

    m = dict(meta)

    def is_valid(val):
        if val is None:
            return False
        try:
            f = float(val)
            return not (math.isnan(f) or math.isinf(f))
        except (ValueError, TypeError):
            return False

    def get_f(key):
        val = m.get(key)
        return float(val) if is_valid(val) else None

    imputed_fields = []

    for _ in range(3):
        initial_count = len(imputed_fields)

        # 1. Price resolution
        price = get_f("currentPrice") or get_f("lastClose")
        if get_f("currentPrice") is None and price is not None:
            m["currentPrice"] = price
            imputed_fields.append("currentPrice")

        # 2. Market Cap & Shares Outstanding
        market_cap = get_f("marketCap")
        shares_out = get_f("sharesOutstanding")
        if market_cap is None and price and price > 0 and shares_out and shares_out > 0:
            m["marketCap"] = price * shares_out
            market_cap = m["marketCap"]
            imputed_fields.append("marketCap")
        if shares_out is None and market_cap and market_cap > 0 and price and price > 0:
            m["sharesOutstanding"] = market_cap / price
            shares_out = m["sharesOutstanding"]
            imputed_fields.append("sharesOutstanding")

        # 3. P/E and Trailing EPS
        pe = get_f("trailingPE")
        eps = get_f("trailingEps")
        if pe is None and price and price > 0 and eps and eps > 0:
            m["trailingPE"] = price / eps
            pe = m["trailingPE"]
            imputed_fields.append("trailingPE")
        if eps is None and price and price > 0 and pe and pe > 0:
            m["trailingEps"] = price / pe
            eps = m["trailingEps"]
            imputed_fields.append("trailingEps")

        # 4. P/B and Book Value (BPS)
        pb = get_f("priceToBook")
        bv = get_f("bookValue")
        if pb is None and price and price > 0 and bv and bv > 0:
            m["priceToBook"] = price / bv
            pb = m["priceToBook"]
            imputed_fields.append("priceToBook")
        if bv is None and price and price > 0 and pb and pb > 0:
            m["bookValue"] = price / pb
            bv = m["bookValue"]
            imputed_fields.append("bookValue")

        # 5. Dividend Rate & Yield & Payout Ratio
        div_rate = get_f("dividendRate")
        div_yield = get_f("dividendYield")
        payout = get_f("payoutRatio")
        if div_rate is None and div_yield and div_yield > 0 and price and price > 0:
            yield_frac = div_yield if div_yield <= 1.0 else div_yield / 100.0
            m["dividendRate"] = price * yield_frac
            div_rate = m["dividendRate"]
            imputed_fields.append("dividendRate")
        if div_yield is None and div_rate and div_rate > 0 and price and price > 0:
            m["dividendYield"] = div_rate / price
            div_yield = m["dividendYield"]
            imputed_fields.append("dividendYield")
        if payout is None and div_rate and div_rate > 0 and eps and eps > 0:
            m["payoutRatio"] = div_rate / eps
            imputed_fields.append("payoutRatio")

        # 6. Revenue & Price to Sales (PSR)
        rev = get_f("totalRevenue")
        ps = get_f("priceToSalesTrailing12Months")
        if ps is None and market_cap and market_cap > 0 and rev and rev > 0:
            m["priceToSalesTrailing12Months"] = market_cap / rev
            ps = m["priceToSalesTrailing12Months"]
            imputed_fields.append("priceToSalesTrailing12Months")
        if rev is None and market_cap and market_cap > 0 and ps and ps > 0:
            m["totalRevenue"] = market_cap / ps
            rev = m["totalRevenue"]
            imputed_fields.append("totalRevenue")

        # 7. Enterprise Value (EV) & EBITDA & Margins
        ev = get_f("enterpriseValue")
        debt = get_f("totalDebt") or 0.0
        cash = get_f("totalCash") or 0.0
        ebitda = get_f("ebitda")
        ebitda_margin = get_f("ebitdaMargins")
        if ev is None and market_cap and market_cap > 0:
            m["enterpriseValue"] = market_cap + debt - cash
            ev = m["enterpriseValue"]
            imputed_fields.append("enterpriseValue")
        if ebitda is None and rev and rev > 0 and ebitda_margin and ebitda_margin > 0:
            m["ebitda"] = rev * ebitda_margin
            ebitda = m["ebitda"]
            imputed_fields.append("ebitda")
        if ebitda_margin is None and ebitda and ebitda > 0 and rev and rev > 0:
            m["ebitdaMargins"] = ebitda / rev
            imputed_fields.append("ebitdaMargins")
        if get_f("enterpriseToEbitda") is None and ev and ev > 0 and ebitda and ebitda > 0:
            m["enterpriseToEbitda"] = ev / ebitda
            imputed_fields.append("enterpriseToEbitda")
        if get_f("enterpriseToRevenue") is None and ev and ev > 0 and rev and rev > 0:
            m["enterpriseToRevenue"] = ev / rev
            imputed_fields.append("enterpriseToRevenue")

        # 8. Debt to Equity (D/E)
        if get_f("debtToEquity") is None and debt > 0 and bv and bv > 0 and shares_out and shares_out > 0:
            equity = bv * shares_out
            if equity > 0:
                m["debtToEquity"] = (debt / equity) * 100.0
                imputed_fields.append("debtToEquity")

        # 9. PEG Ratio
        growth = get_f("earningsGrowth")
        if get_f("pegRatio") is None and pe and pe > 0 and growth and growth != 0:
            growth_pct = growth * 100.0 if abs(growth) < 2.0 else growth
            if growth_pct != 0:
                m["pegRatio"] = pe / growth_pct
                imputed_fields.append("pegRatio")

        # 10. Float Shares
        insider = get_f("heldPercentInsiders")
        if get_f("floatShares") is None and shares_out and shares_out > 0 and insider is not None:
            insider_frac = insider if insider <= 1.0 else insider / 100.0
            m["floatShares"] = shares_out * max(0.0, 1.0 - insider_frac)
            imputed_fields.append("floatShares")

        if len(imputed_fields) == initial_count:
            break

    if imputed_fields:
        logger.info(f"Imputed {len(imputed_fields)} missing metadata metrics: {', '.join(set(imputed_fields))}")

    return m

def update_ticker_metadata(symbol, metadata_root=DEFAULT_METADATA_ROOT, impute_missing=False):
    """Fetch metadata for a ticker and save it as a parquet file."""
    os.makedirs(metadata_root, exist_ok=True)
    
    logger.info(f"Fetching metadata for {symbol} (impute_missing={impute_missing})...")
    provider = StockDataProvider()
    metadata = provider.get_ticker_metadata(symbol)
    
    if metadata.get("status") == "ok":
        if impute_missing:
            metadata = impute_missing_metadata(metadata)
        # Check if local daily parquet exists to compute accurate 52-week high/low
        daily_parquet = os.path.abspath(os.path.join(metadata_root, "..", "Daily", f"{symbol}.parquet"))
        if os.path.exists(daily_parquet):
            try:
                df_daily = pl.read_parquet(daily_parquet)
                if not df_daily.is_empty() and "date" in df_daily.columns:
                    max_date = df_daily["date"].max()
                    cutoff = max_date - datetime.timedelta(days=365)
                    df_year = df_daily.filter(pl.col("date") >= cutoff)
                    if not df_year.is_empty():
                        metadata["fiftyTwoWeekHigh"] = float(df_year["high"].max())
                        metadata["fiftyTwoWeekLow"] = float(df_year["low"].min())
            except Exception as e:
                logger.warning(f"Could not compute 52-week high/low from daily parquet for {symbol}: {e}")

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
    parser.add_argument("--impute-missing", action="store_true", help="Enable estimation/imputation of missing metadata indicators.")
    args = parser.parse_args()

    success = update_ticker_metadata(args.ticker, impute_missing=args.impute_missing)
    if not success:
        import sys
        sys.exit(1)

if __name__ == "__main__":
    main()
