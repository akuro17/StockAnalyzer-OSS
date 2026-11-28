import re
import logging
import pandas as pd
import datetime
import time
import random

# Conditional Import for yfinance (Local/Desktop only)
try:
    import yfinance as yf
except ImportError:
    yf = None

try:
    from zoneinfo import ZoneInfo
except ImportError:
    ZoneInfo = None

# Daily file column contract. The first six columns are the canonical OHLCV columns every time frame shares;
# the three corporate-action columns are provider facts kept in the DAILY file only (weekly/monthly stay at six).
BASE_COLUMNS = ('date', 'open', 'high', 'low', 'close', 'volume')
PRICE_COLUMNS = ('open', 'high', 'low', 'close')
ADJ_CLOSE_COLUMN = 'adj_close'
DIVIDENDS_COLUMN = 'dividends'
STOCK_SPLITS_COLUMN = 'stock_splits'
CORPORATE_ACTION_COLUMNS = (ADJ_CLOSE_COLUMN, DIVIDENDS_COLUMN, STOCK_SPLITS_COLUMN)
DAILY_COLUMNS = BASE_COLUMNS + CORPORATE_ACTION_COLUMNS
# yfinance spellings (after lower-casing) that differ from the stored column names.
PROVIDER_COLUMN_RENAMES = {'adj close': ADJ_CLOSE_COLUMN, 'stock splits': STOCK_SPLITS_COLUMN}
# start_ts that asks for the whole history (yfinance period="max"); any other value is a start time in epoch seconds.
WHOLE_HISTORY_START_TS = 0
# Value of a "no event" corporate-action row (0.0 = no event; a missing column in a stored file is null = not fetched).
NO_EVENT_VALUE = 0.0
# Maximum number of imputed sample dates included in a single OHLCV imputation warning.
MAX_IMPUTATION_SAMPLE_DATES = 5


class StockDataProvider:
    """
    Unified interface for fetching stock data using yfinance.
    Adapted from IaI Web DataIngestion for StockAnalyzer Desktop.
    """

    def __init__(self):
        logging.info("StockDataProvider: Initialized for Desktop (yfinance).")
        self.yf = yf
        if not self.yf:
            logging.warning("StockDataProvider: yfinance module not installed.")

    def get_daily_data(self, symbol, start_ts=WHOLE_HISTORY_START_TS, end_ts=None, delay=None, auto_adjust=False):
        """
        Fetch daily OHLCV data.
        Returns a pandas DataFrame with columns DAILY_COLUMNS:
        ['date', 'open', 'high', 'low', 'close', 'volume', 'adj_close', 'dividends', 'stock_splits'].
        Prices are split-adjusted only (auto_adjust=False). 'adj_close' is the provider's value on the day of the
        download (it differs between download days); a missing or NaN corporate-action value is filled with
        adj_close = close, dividends = 0.0, stock_splits = 0.0.
        """
        is_latest = (end_ts is None)
        if end_ts is None:
            end_ts = int(datetime.datetime.now(datetime.timezone.utc).timestamp())

        return self._get_from_yfinance(symbol, start_ts, end_ts, delay, is_latest, auto_adjust=auto_adjust)

    def _download(self, request_symbol, start_ts, start_date, end_date, is_latest, auto_adjust):
        """One yfinance download in the request shape of the caller (full history, from a date, or a date window)."""
        if start_ts == WHOLE_HISTORY_START_TS:
            return self.yf.download(request_symbol, period="max", progress=False, auto_adjust=auto_adjust, actions=True)
        if is_latest:
            return self.yf.download(request_symbol, start=start_date, progress=False, auto_adjust=auto_adjust, actions=True)
        return self.yf.download(request_symbol, start=start_date, end=end_date, progress=False, auto_adjust=auto_adjust, actions=True)

    def _get_from_yfinance(self, symbol, start_ts, end_ts, delay=None, is_latest=False, auto_adjust=False):
        if not self.yf:
            raise RuntimeError("yfinance module not installed.")

        symbol = symbol.strip().upper()
        if re.match(r'^\d[0-9A-Z]{3}$', symbol):
            symbol = symbol + "-T"

        # Replace trailing hyphenated market suffix (-SS, -SZ, -T, -HK, etc.) with dot (.SS, .SZ, .T, etc.)
        yf_symbol = re.sub(r'-([A-Z]{1,3})$', r'.\1', symbol)

        # Resolve market timezone dynamically
        market_tz = "America/New_York"
        if yf_symbol.endswith(".T"):
            market_tz = "Asia/Tokyo"
        elif yf_symbol.endswith(".SS") or yf_symbol.endswith(".SZ") or yf_symbol.endswith(".HK"):
            market_tz = "Asia/Shanghai"

        if ZoneInfo is not None:
            tz = ZoneInfo(market_tz)
        else:
            tz = datetime.timezone.utc

        # Convert timestamps using the local market timezone
        start_date = datetime.datetime.fromtimestamp(start_ts, tz=datetime.timezone.utc).astimezone(tz).strftime('%Y-%m-%d')
        end_date = datetime.datetime.fromtimestamp(end_ts, tz=datetime.timezone.utc).astimezone(tz).strftime('%Y-%m-%d')
        
        logging.info(f"Fetching data for {symbol} (Yahoo: {yf_symbol}) from {start_date} to {end_date} (TZ: {market_tz})...")

        # Ensure delay to prevent IP block (controlled by caller if delay is set)
        if delay is not None:
            sleep_time = float(delay)
            logging.info(f"Applying custom delay of {sleep_time:.2f} seconds before yfinance daily data request...")
            time.sleep(sleep_time)
        else:
            # Skip sleeping here as outer loop delay is managed on the C# side (delay=0 passed)
            logging.info("Skipping internal delay as delay parameter is None or zero.")

        # yfinance download with exponential backoff retry
        df_raw = None
        for attempt in range(3):
            try:
                df_raw = self._download(yf_symbol, start_ts, start_date, end_date, is_latest, auto_adjust)

                if df_raw is not None and not df_raw.empty:
                    break
            except Exception as e:
                logging.warning(f"yfinance download attempt {attempt + 1} failed for {symbol}: {e}")
            
            if attempt < 2:
                backoff_seconds = min(30.0, (2 ** attempt) * random.uniform(0.8, 1.2))
                time.sleep(backoff_seconds)
        
        if (df_raw is None or df_raw.empty) and yf_symbol != symbol:
            # A trailing "-X" is a market suffix for some tickers (7203-T -> 7203.T) but part of the name of a share class
            # (BRK-B), which Yahoo knows only with the hyphen. When the rewritten spelling returned nothing, retry once with
            # the original spelling.
            logging.info(f"Empty data for {yf_symbol}, retrying once with the original spelling: {symbol}")
            try:
                df_raw = self._download(symbol, start_ts, start_date, end_date, is_latest, auto_adjust)
            except Exception as e:
                logging.warning(f"Original-spelling retry download failed for {symbol}: {e}")

        if df_raw is None or df_raw.empty:
            # Retry with ^ prefix for index symbols (e.g. GSPC -> ^GSPC)
            if not yf_symbol.startswith("^"):
                index_symbol = "^" + yf_symbol
                logging.info(f"Empty data for {symbol}, retrying as index: {index_symbol}")
                try:
                    df_raw = self._download(index_symbol, start_ts, start_date, end_date, is_latest, auto_adjust)
                    if df_raw is not None and not df_raw.empty:
                        logging.warning(f"Resolved {symbol} daily data via index fallback: using {index_symbol}")
                except Exception as e:
                    logging.error(f"Index retry download failed for {index_symbol}: {e}")
                    return None
            if df_raw is None or df_raw.empty:
                logging.warning(f"yfinance returned empty data for {symbol}")
                return None

        # Handle MultiIndex columns in recent yfinance
        if isinstance(df_raw.columns, pd.MultiIndex):
            df_raw.columns = df_raw.columns.droplevel(1)

        # Reset index to get Date column
        df_raw = df_raw.reset_index()
        
        # Lowercase columns for consistency
        df_raw.columns = [PROVIDER_COLUMN_RENAMES.get(str(c).lower(), str(c).lower()) for c in df_raw.columns]

        # Extract required columns
        available_cols = [c for c in BASE_COLUMNS if c in df_raw.columns]
        
        df = df_raw[available_cols].copy()

        # Clean nulls: drop rows with missing date or close price
        if 'close' in df.columns and 'date' in df.columns:
            df = df.dropna(subset=['date', 'close'])

        ohl_cols = [c for c in ('open', 'high', 'low') if c in df.columns]
        missing_open_rows = int(df['open'].isna().sum()) if 'open' in df.columns else 0
        missing_high_rows = int(df['high'].isna().sum()) if 'high' in df.columns else 0
        missing_low_rows = int(df['low'].isna().sum()) if 'low' in df.columns else 0
        missing_ohl_mask = df[ohl_cols].isna().any(axis=1) if ohl_cols else pd.Series(False, index=df.index)
        missing_ohl_rows = int(missing_ohl_mask.sum())
        missing_vol_mask = df['volume'].isna() if 'volume' in df.columns else pd.Series(False, index=df.index)
        missing_vol_rows = int(missing_vol_mask.sum())

        if missing_ohl_rows > 0 or missing_vol_rows > 0:
            any_missing_mask = missing_ohl_mask | missing_vol_mask
            sample_dates = []
            if 'date' in df.columns:
                for date_val in df.loc[any_missing_mask, 'date'].head(MAX_IMPUTATION_SAMPLE_DATES):
                    sample_dates.append(date_val.strftime('%Y-%m-%d') if hasattr(date_val, 'strftime') else str(date_val)[:10])
            logging.warning(
                f"{symbol}: OHLCV imputation applied before writing 6-column Parquet contract: "
                f"missing O/H/L filled from close in {missing_ohl_rows} row(s) "
                f"(open={missing_open_rows}, high={missing_high_rows}, low={missing_low_rows}), "
                f"missing volume filled with 0 in {missing_vol_rows} row(s); "
                f"sample dates: {', '.join(sample_dates) if sample_dates else 'N/A'}"
            )

        if 'volume' in df.columns:
            df['volume'] = df['volume'].fillna(0)
        if 'open' in df.columns and 'close' in df.columns:
            df['open'] = df['open'].fillna(df['close'])
        if 'high' in df.columns and 'close' in df.columns:
            df['high'] = df['high'].fillna(df['close'])
        if 'low' in df.columns and 'close' in df.columns:
            df['low'] = df['low'].fillna(df['close'])

        # Corporate-action columns (aligned on the cleaned rows; absent or NaN values get the documented defaults).
        for column in CORPORATE_ACTION_COLUMNS:
            if column in df_raw.columns:
                values = pd.to_numeric(df_raw[column], errors='coerce').reindex(df.index)
            else:
                values = pd.Series(float('nan'), index=df.index)
            fallback = df['close'] if column == ADJ_CLOSE_COLUMN else NO_EVENT_VALUE
            df[column] = values.fillna(fallback).astype('float64')

        return df

    def get_ticker_metadata(self, symbol):
        """
        Fetch ticker metadata (shortName, sector, industry, etc.) and snapshot.
        Strictly follows the schema defined in Prompt 37-14.
        """
        res = {
            "status": "error",
            "shortName": None,
            "longName": None,
            "sector": "N/A",
            "industry": "N/A",
            "country": "N/A",
            "currency": "N/A",
            "currentPrice": None,
            "lastClose": None,
            "resolvedAsIndex": False,
            "providerSymbol": None,
            "message": ""
        }

        if not self.yf:
            res["message"] = "yfinance module not installed."
            return res
        
        try:
            # Normalize symbol
            symbol = symbol.strip().upper()
            if re.match(r'^\d[0-9A-Z]{3}$', symbol):
                symbol = symbol + "-T"
            yf_symbol = re.sub(r'-([A-Z]{1,3})$', r'.\1', symbol)
            res["providerSymbol"] = yf_symbol
            ticker = self.yf.Ticker(yf_symbol)

            # 1. Fetch info first (single round-trip).
            # If info is empty/missing shortName, retry with ^ prefix for index symbols (e.g. GSPC -> ^GSPC)
            info = {}
            try:
                info = ticker.info or {}
            except Exception as e:
                logging.warning(f"Ticker info fetch failed for {symbol}: {e}")

            if not info.get("shortName") and not yf_symbol.startswith("^"):
                _index_sym = "^" + yf_symbol
                logging.info(f"No shortName for {symbol}, retrying as index: {_index_sym}")
                _index_ticker = self.yf.Ticker(_index_sym)
                try:
                    _index_info = _index_ticker.info or {}
                except Exception as e:
                    logging.warning(f"Index retry info fetch failed for {_index_sym}: {e}")
                    _index_info = {}
                if _index_info.get("shortName"):
                    ticker = _index_ticker
                    info = _index_info
                    res["resolvedAsIndex"] = True
                    res["providerSymbol"] = _index_sym
                    logging.warning(f"Resolved {symbol} metadata via index fallback: using {_index_sym}")

            # 2. Fetch Price Snapshot (Priority: fast_info > info)
            try:
                res["currentPrice"] = ticker.fast_info.get("last_price") or ticker.fast_info.get("lastPrice")
                res["lastClose"] = ticker.fast_info.get("regularMarketPreviousClose") or getattr(ticker.fast_info, "regular_market_previous_close", None)
            except Exception as e:
                logging.debug(f"fast_info fetch failed for {symbol}: {e}")

            # Fallback to history if fast_info fails or returns None
            if res["currentPrice"] is None or res["lastClose"] is None:
                try:
                    hist = ticker.history(period="5d", auto_adjust=False)
                    if not hist.empty:
                        if isinstance(hist.columns, pd.MultiIndex):
                            hist.columns = hist.columns.droplevel(1)
                        if res["currentPrice"] is None:
                            res["currentPrice"] = float(hist["Close"].iloc[-1])
                        if res["lastClose"] is None:
                            res["lastClose"] = float(hist["Close"].iloc[-2] if len(hist) > 1 else hist["Close"].iloc[-1])
                except Exception as e:
                    logging.warning(f"History fallback failed for {symbol}: {e}")


            # Map fields with fallback
            res["shortName"] = info.get("shortName") or info.get("longName") or symbol
            res["longName"] = info.get("longName") or symbol
            res["sector"] = info.get("sector") or "N/A"
            res["industry"] = info.get("industry") or "N/A"
            res["country"] = info.get("country") or info.get("region") or "N/A"
            res["currency"] = info.get("currency") or "USD"

            if res["currentPrice"] is None:
                res["currentPrice"] = info.get("currentPrice") or info.get("regularMarketPrice")

            if res["lastClose"] is None:
                res["lastClose"] = info.get("regularMarketPreviousClose") or info.get("previousClose")

            # Fundamental Metrics
            res["returnOnEquity"] = info.get("returnOnEquity")
            res["returnOnAssets"] = info.get("returnOnAssets")
            res["grossMargins"] = info.get("grossMargins")
            res["operatingMargins"] = info.get("operatingMargins")
            res["profitMargins"] = info.get("profitMargins")
            res["currentRatio"] = info.get("currentRatio")
            res["debtToEquity"] = info.get("debtToEquity")
            res["ebitda"] = info.get("ebitda")
            res["freeCashflow"] = info.get("freeCashflow")
            res["operatingCashflow"] = info.get("operatingCashflow")
            res["trailingPE"] = info.get("trailingPE")
            res["forwardPE"] = info.get("forwardPE")
            res["priceToBook"] = info.get("priceToBook")
            res["trailingEps"] = info.get("trailingEps")
            res["forwardEps"] = info.get("forwardEps")
            res["bookValue"] = info.get("bookValue")
            res["sharesOutstanding"] = info.get("sharesOutstanding")
            res["floatShares"] = info.get("floatShares")
            res["shortRatio"] = info.get("shortRatio")
            res["shortPercentOfFloat"] = info.get("shortPercentOfFloat")
            res["heldPercentInsiders"] = info.get("heldPercentInsiders")
            res["heldPercentInstitutions"] = info.get("heldPercentInstitutions")
            res["longBusinessSummary"] = info.get("longBusinessSummary")
            res["fullTimeEmployees"] = info.get("fullTimeEmployees")
            res["fiftyTwoWeekHigh"] = info.get("fiftyTwoWeekHigh")
            res["fiftyTwoWeekLow"] = info.get("fiftyTwoWeekLow")
            res["revenueGrowth"] = info.get("revenueGrowth")
            res["earningsGrowth"] = info.get("earningsGrowth")
            res["enterpriseValue"] = info.get("enterpriseValue")
            res["enterpriseToEbitda"] = info.get("enterpriseToEbitda")
            res["beta"] = info.get("beta")
            res["payoutRatio"] = info.get("payoutRatio")
            res["dividendRate"] = info.get("dividendRate")
            res["dividendYield"] = info.get("dividendYield")
            res["totalDebt"] = info.get("totalDebt")
            res["totalCash"] = info.get("totalCash")
            res["totalRevenue"] = info.get("totalRevenue")
            res["marketCap"] = info.get("marketCap")
            
            # New fields from docs/webai
            res["pegRatio"] = info.get("pegRatio")
            res["priceToSalesTrailing12Months"] = info.get("priceToSalesTrailing12Months")
            res["enterpriseToRevenue"] = info.get("enterpriseToRevenue")
            res["ebitdaMargins"] = info.get("ebitdaMargins")
            res["quickRatio"] = info.get("quickRatio")
            res["averageVolume"] = info.get("averageVolume") or info.get("averageVolume10days") or info.get("averageDailyVolume10Day")

            # Additional metadata fields
            res["quoteType"] = info.get("quoteType") or "N/A"
            res["exchangeTimezoneName"] = info.get("exchangeTimezoneName") or "N/A"
            res["region"] = info.get("region") or "N/A"
            res["gmtOffSetMilliseconds"] = info.get("gmtOffSetMilliseconds")
            res["exDividendDate"] = info.get("exDividendDate")
            res["lastFiscalYearEnd"] = info.get("lastFiscalYearEnd")
            res["mostRecentQuarter"] = info.get("mostRecentQuarter")
            res["targetHighPrice"] = info.get("targetHighPrice")
            res["targetLowPrice"] = info.get("targetLowPrice")
            res["targetMeanPrice"] = info.get("targetMeanPrice")
            res["targetMedianPrice"] = info.get("targetMedianPrice")
            res["recommendationKey"] = info.get("recommendationKey") or "N/A"
            res["recommendationMean"] = info.get("recommendationMean")
            res["numberOfAnalystOpinions"] = info.get("numberOfAnalystOpinions")

            res["status"] = "ok"
            return res

        except Exception as e:
            msg = f"yfinance metadata fetch failed for {symbol}: {str(e)}"
            logging.error(msg)
            res["message"] = msg
            return res
