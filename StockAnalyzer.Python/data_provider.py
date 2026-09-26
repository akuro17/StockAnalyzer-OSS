import os
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

    def get_daily_data(self, symbol, start_ts=0, end_ts=None, delay=None):
        """
        Fetch daily OHLCV data.
        Returns a pandas DataFrame with columns: ['date', 'open', 'high', 'low', 'close', 'volume']
        """
        is_latest = (end_ts is None)
        if end_ts is None:
            end_ts = int(datetime.datetime.now(datetime.timezone.utc).timestamp())

        return self._get_from_yfinance(symbol, start_ts, end_ts, delay, is_latest)

    def _get_from_yfinance(self, symbol, start_ts, end_ts, delay=None, is_latest=False):
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
                if start_ts == 0:
                     df_raw = self.yf.download(yf_symbol, period="max", progress=False, auto_adjust=True)
                elif is_latest:
                     df_raw = self.yf.download(yf_symbol, start=start_date, progress=False, auto_adjust=True)
                else:
                     df_raw = self.yf.download(yf_symbol, start=start_date, end=end_date, progress=False, auto_adjust=True)
                
                if df_raw is not None and not df_raw.empty:
                    break
            except Exception as e:
                logging.warning(f"yfinance download attempt {attempt + 1} failed for {symbol}: {e}")
            
            if attempt < 2:
                backoff_seconds = min(30.0, (2 ** attempt) * random.uniform(0.8, 1.2))
                time.sleep(backoff_seconds)
        
        if df_raw is None or df_raw.empty:
            # Retry with ^ prefix for index symbols (e.g. GSPC -> ^GSPC)
            if not yf_symbol.startswith("^"):
                index_symbol = "^" + yf_symbol
                logging.info(f"Empty data for {symbol}, retrying as index: {index_symbol}")
                try:
                    if start_ts == 0:
                        df_raw = self.yf.download(index_symbol, period="max", progress=False, auto_adjust=True)
                    elif is_latest:
                        df_raw = self.yf.download(index_symbol, start=start_date, progress=False, auto_adjust=True)
                    else:
                        df_raw = self.yf.download(index_symbol, start=start_date, end=end_date, progress=False, auto_adjust=True)
                except Exception as e:
                    logging.error(f"Index retry download failed for {index_symbol}: {e}")
                    return None
            if df_raw.empty:
                logging.warning(f"yfinance returned empty data for {symbol}")
                return None

        # Handle MultiIndex columns in recent yfinance
        if isinstance(df_raw.columns, pd.MultiIndex):
            df_raw.columns = df_raw.columns.droplevel(1)

        # Reset index to get Date column
        df_raw = df_raw.reset_index()
        
        # Lowercase columns for consistency
        df_raw.columns = [str(c).lower() for c in df_raw.columns]
        
        # Extract required columns
        required_cols = ['date', 'open', 'high', 'low', 'close', 'volume']
        available_cols = [c for c in required_cols if c in df_raw.columns]
        
        df = df_raw[available_cols].copy()

        # Clean nulls: drop rows with missing date or close price
        if 'close' in df.columns and 'date' in df.columns:
            df = df.dropna(subset=['date', 'close'])
        if 'volume' in df.columns:
            df['volume'] = df['volume'].fillna(0)
        if 'open' in df.columns and 'close' in df.columns:
            df['open'] = df['open'].fillna(df['close'])
        if 'high' in df.columns and 'close' in df.columns:
            df['high'] = df['high'].fillna(df['close'])
        if 'low' in df.columns and 'close' in df.columns:
            df['low'] = df['low'].fillna(df['close'])

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
                    logging.info(f"Resolved {symbol} as index: using {_index_sym}")

            # 2. Fetch Price Snapshot (Priority: fast_info > info)
            try:
                res["currentPrice"] = ticker.fast_info.get("last_price") or ticker.fast_info.get("lastPrice")
            except Exception as e:
                logging.debug(f"fast_info fetch failed for {symbol}: {e}")

            # Fallback to history if fast_info fails or returns None
            if res["currentPrice"] is None:
                try:
                    hist = ticker.history(period="5d")
                    if not hist.empty:
                        if isinstance(hist.columns, pd.MultiIndex):
                            hist.columns = hist.columns.droplevel(1)
                        res["currentPrice"] = float(hist["Close"].iloc[-1])
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
                res["lastClose"] = info.get("previousClose") or info.get("regularMarketPreviousClose")

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
            
            # New fields from Y:\Temp\webai.txt
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
