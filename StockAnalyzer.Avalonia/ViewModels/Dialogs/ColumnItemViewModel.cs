using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Models.Watchlist;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs;

public enum ColumnCategory
{
    Active,
    All,
    Basic,
    PriceVolume,
    Valuation,
    Ratio,
    Financial
}

/// <summary>
/// ViewModel representing a single column item in the Column Chooser dialog.
/// </summary>
public partial class ColumnItemViewModel : ViewModelBase
{
    [ObservableProperty]
    private bool _isActive;

    public string MemberName { get; }
    public string HeaderKey { get; }
    public string EnglishName { get; }
    public string Description { get; }
    public string Formula { get; }
    public ColumnCategory Category { get; }

    public bool IsSymbol => MemberName == "Symbol";
    public bool IsSelect => MemberName == "IsChecked";

    private readonly string? _localizedHeader;
    public string LocalizedHeader => _localizedHeader ?? EnglishName;

    public ColumnItemViewModel(
        string memberName,
        string headerKey,
        bool isActive,
        ColumnCategory category,
        string englishName,
        string description,
        string formula,
        string? localizedHeader = null)
    {
        MemberName = memberName;
        HeaderKey = headerKey;
        _isActive = isActive;
        Category = category;
        EnglishName = englishName;
        Description = description;
        Formula = formula;
        _localizedHeader = localizedHeader;
    }

    private record ColumnStaticInfo(ColumnCategory Category, string EnglishName, string Description, string Formula);

    private static readonly Dictionary<string, ColumnStaticInfo> InfoMap = new(StringComparer.OrdinalIgnoreCase)
    {
        { "IsChecked", new ColumnStaticInfo(ColumnCategory.Basic, "Select", "Row selection checkbox for bulk actions", "Checkbox State") },
        { "Symbol", new ColumnStaticInfo(ColumnCategory.Basic, "Symbol", "The unique ticker symbol identifying the stock", "Ticker Symbol") },
        { "Name", new ColumnStaticInfo(ColumnCategory.Basic, "Name", "Full official name of the company", "Company Name") },
        { "Sector", new ColumnStaticInfo(ColumnCategory.Basic, "Sector", "Broad sector classification of the market", "GICS Sector") },
        { "Industry", new ColumnStaticInfo(ColumnCategory.Basic, "Industry", "Specific industry group classification", "GICS Industry") },
        { "Open", new ColumnStaticInfo(ColumnCategory.PriceVolume, "Open", "Opening price for the current trading day", "Open Price") },
        { "High", new ColumnStaticInfo(ColumnCategory.PriceVolume, "High", "Highest traded price during the current day", "High Price") },
        { "Low", new ColumnStaticInfo(ColumnCategory.PriceVolume, "Low", "Lowest traded price during the current day", "Low Price") },
        { "Close", new ColumnStaticInfo(ColumnCategory.PriceVolume, "Close", "Closing price or latest market price of the stock", "Close Price") },
        { "Volume", new ColumnStaticInfo(ColumnCategory.PriceVolume, "Volume", "Total number of shares traded during the current day", "Sum(Shares Traded)") },
        { "Change", new ColumnStaticInfo(ColumnCategory.PriceVolume, "Change", "Absolute price change compared to the previous day's close", "Close - Previous Close") },
        { "ChangePercent", new ColumnStaticInfo(ColumnCategory.PriceVolume, "Change %", "Percentage price change compared to the previous day's close", "((Close - PrevClose) / PrevClose) * 100") },
        
        { "ReturnOnEquity", new ColumnStaticInfo(ColumnCategory.Ratio, "ROE", "Return on Equity: Measures profitability relative to shareholder equity", "(TTM Net Income / Average Shareholders' Equity) * 100") },
        { "ReturnOnAssets", new ColumnStaticInfo(ColumnCategory.Ratio, "ROA", "Return on Assets: Measures how efficiently assets are used to generate profit", "(TTM Net Income / Average Total Assets) * 100") },
        { "GrossMargins", new ColumnStaticInfo(ColumnCategory.Ratio, "Gross Margin", "Gross profit as a percentage of total revenue", "(Gross Profit / Total Revenue) * 100") },
        { "OperatingMargins", new ColumnStaticInfo(ColumnCategory.Ratio, "Operating Margin", "Operating income as a percentage of total revenue", "(Operating Income / Total Revenue) * 100") },
        { "ProfitMargins", new ColumnStaticInfo(ColumnCategory.Ratio, "Profit Margin", "Net income as a percentage of total revenue", "(Net Income / Total Revenue) * 100") },
        { "CurrentRatio", new ColumnStaticInfo(ColumnCategory.Ratio, "Current Ratio", "Liquidity ratio measuring ability to pay short-term obligations", "Total Current Assets / Total Current Liabilities") },
        { "DebtToEquity", new ColumnStaticInfo(ColumnCategory.Ratio, "D/E Ratio", "Debt-to-Equity: Measures financial leverage of the company", "Total Debt / Shareholders' Equity") },
        
        { "Ebitda", new ColumnStaticInfo(ColumnCategory.Financial, "EBITDA", "Earnings Before Interest, Taxes, Depreciation, and Amortization", "Operating Income + Depreciation & Amortization") },
        { "FreeCashflow", new ColumnStaticInfo(ColumnCategory.Financial, "Free Cash Flow", "Cash generated by the company after capital expenditures (FCF)", "Operating Cash Flow - Capital Expenditures") },
        { "OperatingCashflow", new ColumnStaticInfo(ColumnCategory.Financial, "Operating Cash Flow", "Total cash generated from core business operations (OCF)", "Cash from Operations (Indirect Method)") },
        
        { "TrailingPE", new ColumnStaticInfo(ColumnCategory.Valuation, "Trailing P/E", "Price-to-Earnings ratio (PER) based on trailing 12-month earnings", "Latest Share Price / TTM EPS") },
        { "ForwardPE", new ColumnStaticInfo(ColumnCategory.Valuation, "Forward P/E", "Price-to-Earnings ratio (PER) based on forecasted earnings", "Latest Share Price / Estimated Next 12M EPS") },
        { "PriceToBook", new ColumnStaticInfo(ColumnCategory.Valuation, "P/B Ratio", "Price-to-Book ratio (PBR): Compares market value to book value of equity", "Latest Share Price / Book Value per Share") },
        { "TrailingEps", new ColumnStaticInfo(ColumnCategory.Valuation, "Trailing EPS", "Earnings Per Share over the trailing 12 months", "TTM Net Income / Shares Outstanding") },
        { "ForwardEps", new ColumnStaticInfo(ColumnCategory.Valuation, "Forward EPS", "Forecasted Earnings Per Share for the next fiscal period", "Estimated Net Income / Shares Outstanding") },
        { "BookValue", new ColumnStaticInfo(ColumnCategory.Valuation, "Book Value", "Net asset value of a company on a per-share basis (BPS)", "Shareholders' Equity / Shares Outstanding") },
        
        { "SharesOutstanding", new ColumnStaticInfo(ColumnCategory.Financial, "Shares Outstanding", "Total number of shares held by all shareholders", "Total Issued Shares") },
        { "FloatShares", new ColumnStaticInfo(ColumnCategory.Financial, "Float Shares", "Shares available for public trading excluding insider holdings", "Shares Outstanding - Restricted Shares") },
        { "ShortRatio", new ColumnStaticInfo(ColumnCategory.Financial, "Days to Cover", "Estimated days to cover shorted shares based on average volume", "Short Interest (Shares) / Avg Daily Volume (Shares)") },
        { "ShortPercentOfFloat", new ColumnStaticInfo(ColumnCategory.Financial, "Short Interest % of Float", "Percentage of public float shares currently shorted", "(Short Interest / Float Shares) * 100") },
        { "HeldPercentInsiders", new ColumnStaticInfo(ColumnCategory.Financial, "Insider Ownership %", "Percentage of shares held by company insiders", "(Insider Shares / Shares Outstanding) * 100") },
        { "HeldPercentInstitutions", new ColumnStaticInfo(ColumnCategory.Financial, "Institutional Ownership %", "Percentage of shares held by institutional investors", "(Institutional Shares / Shares Outstanding) * 100") },
        
        { "LongBusinessSummary", new ColumnStaticInfo(ColumnCategory.Basic, "Business Summary", "Comprehensive summary of the company's business activities", "Long Business Description") },
        { "FullTimeEmployees", new ColumnStaticInfo(ColumnCategory.Financial, "Employees", "Number of full-time employees", "Total Employee Count") },
        { "FiftyTwoWeekHigh", new ColumnStaticInfo(ColumnCategory.Financial, "52-Week High", "Highest stock price in the last 52 weeks (52W High)", "Max(High Price, Last 52 Weeks)") },
        { "FiftyTwoWeekLow", new ColumnStaticInfo(ColumnCategory.Financial, "52-Week Low", "Lowest stock price in the last 52 weeks (52W Low)", "Min(Low Price, Last 52 Weeks)") },
        { "RevenueGrowth", new ColumnStaticInfo(ColumnCategory.Financial, "Revenue Growth", "Quarterly revenue growth compared to previous year (YoY)", "((Revenue_t - Revenue_{t-4Q}) / |Revenue_{t-4Q}|) * 100") },
        { "EarningsGrowth", new ColumnStaticInfo(ColumnCategory.Financial, "Earnings Growth", "Quarterly net income growth compared to previous year (YoY)", "((NetIncome_t - NetIncome_{t-4Q}) / |NetIncome_{t-4Q}|) * 100") },
        
        { "EnterpriseValue", new ColumnStaticInfo(ColumnCategory.Valuation, "EV", "Enterprise Value: Total theoretical takeover price of the firm", "Market Cap + Total Debt - Cash & Equivalents") },
        { "EnterpriseToEbitda", new ColumnStaticInfo(ColumnCategory.Valuation, "EV/EBITDA", "Enterprise Value divided by EBITDA", "Enterprise Value / EBITDA") },
        { "Beta", new ColumnStaticInfo(ColumnCategory.Financial, "Beta", "Systematic risk/volatility of stock relative to market index", "Covariance(Stock, Market) / Variance(Market)") },
        { "PayoutRatio", new ColumnStaticInfo(ColumnCategory.Ratio, "Payout Ratio", "Percentage of net income paid out as dividends to shareholders", "(Dividend per Share / Earnings per Share) * 100") },
        { "DividendRate", new ColumnStaticInfo(ColumnCategory.Financial, "Dividend per Share", "Annualized dividend amount paid per share (DPS)", "Total Dividends Paid (TTM) / Shares Outstanding") },
        { "DividendYield", new ColumnStaticInfo(ColumnCategory.Ratio, "Dividend Yield", "Annualized dividend yield percentage relative to stock price", "(Annual Dividend / Share Price) * 100") },
        { "TotalDebt", new ColumnStaticInfo(ColumnCategory.Financial, "Total Debt", "Sum of all short-term and long-term liabilities", "Short-term Debt + Long-term Debt") },
        { "TotalCash", new ColumnStaticInfo(ColumnCategory.Financial, "Total Cash", "Total cash and cash equivalents on balance sheet", "Cash + Cash Equivalents") },
        { "TotalRevenue", new ColumnStaticInfo(ColumnCategory.Financial, "Total Revenue", "Total sales revenue generated by the company", "Reported Revenue (TTM)") },
        { "MarketCap", new ColumnStaticInfo(ColumnCategory.Financial, "Market Cap", "Total equity market value of the company (MC / Market Capitalization)", "Share Price * Shares Outstanding") },
        
        { "PbrCalculated", new ColumnStaticInfo(ColumnCategory.Valuation, "P/B (Live)", "Live calculated Price-to-Book ratio based on latest price", "Latest Share Price / Book Value per Share") },
        { "DividendYieldCalculated", new ColumnStaticInfo(ColumnCategory.Ratio, "Div Yield (Live)", "Live calculated Dividend Yield based on latest price", "(Annualized Dividend / Latest Share Price) * 100") },
        { "EarningsYield", new ColumnStaticInfo(ColumnCategory.Valuation, "Earnings Yield", "The inverse of P/E ratio, showing earnings efficiency", "EPS / Share Price") },
        { "FcfYield", new ColumnStaticInfo(ColumnCategory.Valuation, "FCF Yield", "Free Cash Flow yield comparing FCF to market cap", "Free Cash Flow / Market Cap (Equity Yield)") },
        { "FcfMargin", new ColumnStaticInfo(ColumnCategory.Ratio, "FCF Margin", "Free Cash Flow margin as a percentage of revenue", "(Free Cash Flow / Total Revenue) * 100") },
        { "NetDebt", new ColumnStaticInfo(ColumnCategory.Financial, "Net Debt", "Net outstanding debt of the company", "Total Debt - Total Cash") },
        { "NetDebtToEbitda", new ColumnStaticInfo(ColumnCategory.Ratio, "Net Debt / EBITDA", "Net debt relative to EBITDA earnings", "Net Debt / EBITDA") },
        { "DividendCoverage", new ColumnStaticInfo(ColumnCategory.Ratio, "Div Coverage", "Measures ability to pay dividends from current earnings", "Earnings per Share / Dividend per Share") },
        { "PctFromFiftyTwoWeekHigh", new ColumnStaticInfo(ColumnCategory.Ratio, "% from 52W High", "Percentage distance from the 52-week high price", "((Latest Price - 52W High) / 52W High) * 100") },
        { "FloatRatio", new ColumnStaticInfo(ColumnCategory.Ratio, "Float Ratio", "Proportion of shares outstanding available in public market", "(Float Shares / Shares Outstanding) * 100") },
        { "MarketCapPerEmployee", new ColumnStaticInfo(ColumnCategory.Financial, "Market Cap / Employee", "Market capitalization generated per employee", "Market Cap / Total Employee Count") },
        { "PegRatio", new ColumnStaticInfo(ColumnCategory.Valuation, "PEG Ratio", "P/E ratio adjusted for forecasted growth rate", "Forward P/E / Expected EPS Growth Rate (%)") },
        { "OperatingCashFlowYield", new ColumnStaticInfo(ColumnCategory.Ratio, "OCF Yield", "Operating Cash Flow relative to market cap", "Operating Cash Flow / Market Cap") },
        { "NetCashRatio", new ColumnStaticInfo(ColumnCategory.Ratio, "Net Cash Ratio", "Cash asset ratio of the company", "(Total Cash - Total Debt) / Market Cap") },
        { "PriceToSalesTrailing12Months", new ColumnStaticInfo(ColumnCategory.Valuation, "P/S (TTM)", "Price-to-Sales trailing 12 months", "Share Price / Revenue per Share") },
        { "EnterpriseToRevenue", new ColumnStaticInfo(ColumnCategory.Valuation, "EV/Revenue", "EV relative to total revenue", "Enterprise Value / Total Revenue") },
        { "EbitdaMargins", new ColumnStaticInfo(ColumnCategory.Ratio, "EBITDA Margin", "EBITDA earnings margin percentage", "(EBITDA / Total Revenue) * 100") },
        { "QuickRatio", new ColumnStaticInfo(ColumnCategory.Ratio, "Quick Ratio", "Acid-test liquidity ratio using most liquid assets", "(Current Assets - Inventory) / Current Liabilities") },
        { "AverageVolume", new ColumnStaticInfo(ColumnCategory.Financial, "Avg Volume", "Average daily trading volume", "Average(Daily Volume)") },
        { "PriceToCashFlowRatio", new ColumnStaticInfo(ColumnCategory.Valuation, "P/CF Ratio", "Price to Operating Cash Flow ratio", "Share Price / Operating Cash Flow per Share") },
        { "NetDebtEquityRatio", new ColumnStaticInfo(ColumnCategory.Ratio, "Net D/E Ratio", "Net debt relative to total equity", "Net Debt / Shareholders' Equity") },
        { "FiftyTwoWeekRangePosition", new ColumnStaticInfo(ColumnCategory.Valuation, "52W Range Pos.", "Relative percentile position within the 52-week range", "(Price - 52W Low) / (52W High - 52W Low)") },
        { "DailyTurnoverRate", new ColumnStaticInfo(ColumnCategory.Financial, "Turnover Rate", "Percentage of outstanding shares traded on current day", "(Daily Volume / Shares Outstanding) * 100") },
        { "AverageTurnoverRate", new ColumnStaticInfo(ColumnCategory.Financial, "Avg Turnover Rate", "Average daily share turnover percentage", "Average(Daily Turnover Rate)") },
        { "DailyFloatShareTurnoverRatio", new ColumnStaticInfo(ColumnCategory.Financial, "Float Turnover Rate", "Percentage of float shares traded on current day", "(Daily Volume / Float Shares) * 100") },
        { "AverageFloatTurnover", new ColumnStaticInfo(ColumnCategory.Financial, "Avg Float Turnover", "Average daily float share turnover percentage", "Average(Float Turnover Rate)") },
        
        { "ExDividendDate", new ColumnStaticInfo(ColumnCategory.Financial, "Ex-Dividend Date", "The date on which a stock begins trading without the next dividend payment", "Raw date from yfinance") },
        { "ExchangeTimezoneName", new ColumnStaticInfo(ColumnCategory.Basic, "Exchange Timezone", "The timezone name of the stock exchange where the ticker is listed", "Raw string from yfinance") },
        { "GmtOffSetMilliseconds", new ColumnStaticInfo(ColumnCategory.Basic, "GMT Offset (ms)", "The GMT offset in milliseconds for the exchange's local time", "Raw integer value") },
        { "LastFiscalYearEnd", new ColumnStaticInfo(ColumnCategory.Financial, "Last Fiscal Year End", "The end date of the last reported fiscal year", "Raw date from yfinance") },
        { "MostRecentQuarter", new ColumnStaticInfo(ColumnCategory.Financial, "Most Recent Quarter", "The end date of the most recently reported quarter", "Raw date from yfinance") },
        { "NumberOfAnalystOpinions", new ColumnStaticInfo(ColumnCategory.Valuation, "Analyst Opinions Count", "Total number of stock analysts who have submitted recommendations/target prices", "Raw count from yfinance") },
        { "QuoteType", new ColumnStaticInfo(ColumnCategory.Basic, "Quote Type", "The asset class type of the ticker (e.g. EQUITY, ETF)", "Raw string from yfinance") },
        { "RecommendationKey", new ColumnStaticInfo(ColumnCategory.Valuation, "Recommendation", "Consensus recommendation by analysts (e.g., buy, hold, underperform)", "Consensus recommendation string") },
        { "RecommendationMean", new ColumnStaticInfo(ColumnCategory.Valuation, "Rec. Mean", "Average analyst recommendation score (typically 1.0 Strong Buy to 5.0 Strong Sell)", "Average score of all recommendations") },
        { "Region", new ColumnStaticInfo(ColumnCategory.Basic, "Region", "The geographical region of the stock issuer", "Raw region from yfinance") },
        { "TargetHighPrice", new ColumnStaticInfo(ColumnCategory.Valuation, "Target High", "Highest analyst price target for the stock over the next 12 months", "Max(Estimated price targets)") },
        { "TargetLowPrice", new ColumnStaticInfo(ColumnCategory.Valuation, "Target Low", "Lowest analyst price target for the stock over the next 12 months", "Min(Estimated price targets)") },
        { "TargetMeanPrice", new ColumnStaticInfo(ColumnCategory.Valuation, "Target Mean", "Average analyst price target for the stock over the next 12 months", "Average(Estimated price targets)") },
        { "TargetMedianPrice", new ColumnStaticInfo(ColumnCategory.Valuation, "Target Median", "Median analyst price target for the stock over the next 12 months", "Median(Estimated price targets)") },
        
        { "MetadataLastUpdated", new ColumnStaticInfo(ColumnCategory.Basic, "Last Updated", "Timestamp of the last metadata synchronization (excludes time series update time)", "Metadata Retrieval Timestamp (UTC)") },
        { "Tag", new ColumnStaticInfo(ColumnCategory.Basic, "Tag", "User-defined custom tags. For multiple tags, use comma separation (e.g. Growth,Tech)", "User Input String") }
    };

    public static ColumnItemViewModel Create(WatchlistColumnMetadata metadata, bool isActive, string? localizedHeader = null)
    {
        var member = metadata.MemberName;
        if (InfoMap.TryGetValue(member, out var info))
        {
            return new ColumnItemViewModel(
                member,
                metadata.HeaderKey,
                isActive,
                info.Category,
                info.EnglishName,
                info.Description,
                info.Formula,
                localizedHeader
            );
        }

        // Fallback info for undefined metadata
        return new ColumnItemViewModel(
            member,
            metadata.HeaderKey,
            isActive,
            ColumnCategory.Basic,
            member,
            $"Description for {member}",
            "N/A",
            localizedHeader
        );
    }
}
