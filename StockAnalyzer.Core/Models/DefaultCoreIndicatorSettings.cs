using System.Collections.Generic;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Chart;
using StockAnalyzer.Core.Models.Indicators.Oscillators;
using StockAnalyzer.Core.Models.Indicators.Statistics;
using StockAnalyzer.Core.Models.Indicators.Trend;
using StockAnalyzer.Core.Models.Indicators.Volume;
using StockAnalyzer.Core.Models.Indicators.Volatility;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models;

public static class DefaultCoreIndicatorSettings
{
    public static List<CoreIndicatorSettings> GetDefault()
    {
        return new List<CoreIndicatorSettings>
        {
            new CoreIndicatorSettings 
            { 
                TypeEnum = IndicatorType.SMA, 
                Category = CoreIndicatorCategory.Trend, 
                IsEnabled = false, 
                ParameterObject = new CoreSmaParameter { Period = IndicatorDefaultConstants.SmaPeriod }, 
                Color = IndicatorDefaultConstants.SmaColor, 
                Thickness = IndicatorDefaultConstants.DefaultOverlayThickness, 
                Style = CoreLineStyle.Solid 
            },
            new CoreIndicatorSettings 
            { 
                TypeEnum = IndicatorType.EMA, 
                Category = CoreIndicatorCategory.Trend, 
                IsEnabled = false, 
                ParameterObject = new CoreEmaParameter { Period = IndicatorDefaultConstants.EmaPeriod }, 
                Color = IndicatorDefaultConstants.EmaColor, 
                Thickness = IndicatorDefaultConstants.DefaultOverlayThickness, 
                Style = CoreLineStyle.Solid 
            },
            new CoreIndicatorSettings 
            { 
                TypeEnum = IndicatorType.KAMA, 
                Category = CoreIndicatorCategory.Trend, 
                IsEnabled = false, 
                ParameterObject = new CoreKamaParameter 
                { 
                    Period = IndicatorDefaultConstants.KamaPeriod, 
                    Fast = IndicatorDefaultConstants.KamaFastPeriod, 
                    Slow = IndicatorDefaultConstants.KamaSlowPeriod 
                }, 
                Color = IndicatorDefaultConstants.KamaColor, 
                Thickness = IndicatorDefaultConstants.DefaultOverlayThickness, 
                Style = CoreLineStyle.Solid,
                IsOverlay = true
            },
            new CoreIndicatorSettings 
            { 
                TypeEnum = IndicatorType.BB, 
                Category = CoreIndicatorCategory.Trend, 
                IsEnabled = false, 
                ParameterObject = new CoreBollingerBandsParameter { Period = IndicatorDefaultConstants.BollingerPeriod, StdDevMultiplier = IndicatorDefaultConstants.BollingerStdDevMultiplier }, 
                Color = IndicatorDefaultConstants.BollingerMiddleColor, 
                Thickness = IndicatorDefaultConstants.DefaultBandThickness, 
                Style = CoreLineStyle.Solid,
                SeriesColors = new System.Collections.ObjectModel.ObservableCollection<SeriesColorConfig> 
                {
                    new SeriesColorConfig { Name = "Middle", DisplayName = "Middle Band", Color = IndicatorDefaultConstants.BollingerMiddleColor, TargetSeries = new List<string> { "Main" } },
                    new SeriesColorConfig { Name = "Bands", DisplayName = "Bands (Upper/Lower)", Color = IndicatorDefaultConstants.BollingerBandsColor, TargetSeries = new List<string> { "Upper", "Lower" } }
                }
            },
            new CoreIndicatorSettings 
            { 
                TypeEnum = IndicatorType.Donchian, 
                Category = CoreIndicatorCategory.Volatility, 
                IsEnabled = false, 
                ParameterObject = new CoreSmaParameter { Period = 20 }, 
                Color = IndicatorDefaultConstants.SmaColor, 
                Thickness = IndicatorDefaultConstants.DefaultBandThickness, 
                Style = CoreLineStyle.Solid,
                IsOverlay = true,
                SeriesColors = new System.Collections.ObjectModel.ObservableCollection<SeriesColorConfig> 
                {
                    new SeriesColorConfig { Name = "Upper", DisplayName = "Upper Band", Color = IndicatorDefaultConstants.BollingerBandsColor, TargetSeries = new List<string> { nameof(CoreDonchianChannelIndicator.UpperBand) } },
                    new SeriesColorConfig { Name = "Middle", DisplayName = "Middle Line", Color = IndicatorDefaultConstants.SmaColor, TargetSeries = new List<string> { IndicatorResult.MainSeriesName } },
                    new SeriesColorConfig { Name = "Lower", DisplayName = "Lower Band", Color = IndicatorDefaultConstants.BollingerBandsColor, TargetSeries = new List<string> { nameof(CoreDonchianChannelIndicator.LowerBand) } }
                }
            },
            new CoreIndicatorSettings
            {
                TypeEnum = IndicatorType.Keltner,
                Category = CoreIndicatorCategory.Volatility,
                IsEnabled = false,
                ParameterObject = new CoreKeltnerParameter(),
                Color = IndicatorDefaultConstants.BollingerMiddleColor,
                Thickness = IndicatorDefaultConstants.DefaultOverlayThickness,
                Style = CoreLineStyle.Solid,
                IsOverlay = true,
                SeriesColors = new System.Collections.ObjectModel.ObservableCollection<SeriesColorConfig>
                {
                    new SeriesColorConfig { Name = IndicatorResult.MainSeriesName, DisplayName = "Middle Line", Color = IndicatorDefaultConstants.BollingerMiddleColor, TargetSeries = new List<string> { IndicatorResult.MainSeriesName } },
                    new SeriesColorConfig { Name = nameof(CoreKeltnerChannelIndicator.UpperBand), DisplayName = "Upper Band", Color = IndicatorDefaultConstants.BollingerBandsColor, TargetSeries = new List<string> { nameof(CoreKeltnerChannelIndicator.UpperBand) } },
                    new SeriesColorConfig { Name = nameof(CoreKeltnerChannelIndicator.LowerBand), DisplayName = "Lower Band", Color = IndicatorDefaultConstants.BollingerBandsColor, TargetSeries = new List<string> { nameof(CoreKeltnerChannelIndicator.LowerBand) } }
                }
            },
            new CoreIndicatorSettings
            {
                TypeEnum = IndicatorType.Ichimoku,
                Category = CoreIndicatorCategory.Trend, 
                IsEnabled = false, 
                ParameterObject = new CoreIchimokuParameter(), 
                Color = IndicatorDefaultConstants.IchimokuBaseColor, 
                Thickness = IndicatorDefaultConstants.DefaultBandThickness, 
                Style = CoreLineStyle.Solid,
                 SeriesColors = new System.Collections.ObjectModel.ObservableCollection<SeriesColorConfig> 
                {
                    new SeriesColorConfig { Name = "Tenkan", DisplayName = "Tenkan-sen", Color = IndicatorDefaultConstants.IchimokuTenkanColor, TargetSeries = new List<string> { "Main" } },
                    new SeriesColorConfig { Name = "Kijun", DisplayName = "Kijun-sen", Color = IndicatorDefaultConstants.IchimokuKijunColor, TargetSeries = new List<string> { CoreIchimokuIndicator.KijunSenSeriesName } },
                    new SeriesColorConfig { Name = "Chikou", DisplayName = "Chikou Span", Color = IndicatorDefaultConstants.IchimokuChikouColor, TargetSeries = new List<string> { CoreIchimokuIndicator.ChikouSpanSeriesName } },
                    new SeriesColorConfig { Name = "SenkouA", DisplayName = "Senkou Span A", Color = IndicatorDefaultConstants.IchimokuSenkouAColor, TargetSeries = new List<string> { CoreIchimokuIndicator.SenkouSpanASeriesName } },
                    new SeriesColorConfig { Name = "SenkouB", DisplayName = "Senkou Span B", Color = IndicatorDefaultConstants.IchimokuSenkouBColor, TargetSeries = new List<string> { CoreIchimokuIndicator.SenkouSpanBSeriesName } },
                }
            },
            new CoreIndicatorSettings 
            { 
                TypeEnum = IndicatorType.ParabolicSAR, 
                Category = CoreIndicatorCategory.Trend, 
                IsEnabled = false, 
                ParameterObject = new CoreParabolicSarParameter { AccelerationStart = IndicatorDefaultConstants.ParabolicAccelerationStart, AccelerationStep = IndicatorDefaultConstants.ParabolicAccelerationStep, AccelerationMax = IndicatorDefaultConstants.ParabolicAccelerationMax }, 
                Color = IndicatorDefaultConstants.ParabolicColor,
                Thickness = IndicatorDefaultConstants.DefaultOverlayThickness, 
                Style = CoreLineStyle.Dot,
                IsOverlay = true,
                UseUpDownColors = true,
                UpColor = IndicatorDefaultConstants.ParabolicUpColor,
                DownColor = IndicatorDefaultConstants.ParabolicDownColor
            },
            new CoreIndicatorSettings 
            { 
                TypeEnum = IndicatorType.RSI, 
                Category = CoreIndicatorCategory.Oscillator, 
                IsEnabled = false, 
                ParameterObject = new CoreRsiParameter { Period = IndicatorDefaultConstants.RsiPeriod }, 
                Color = IndicatorDefaultConstants.RsiColor, 
                Thickness = IndicatorDefaultConstants.DefaultSubPanelThickness, 
                Style = CoreLineStyle.Solid, 
                IsOverlay = false,
                MinValue = IndicatorDefaultConstants.RsiMinValue,
                MaxValue = IndicatorDefaultConstants.RsiMaxValue
            },
            new CoreIndicatorSettings 
            { 
                TypeEnum = IndicatorType.MACD, 
                Category = CoreIndicatorCategory.Oscillator, 
                IsEnabled = false, 
                ParameterObject = new CoreMacdParameter(), 
                Color = IndicatorDefaultConstants.MacdLineColor, 
                Thickness = IndicatorDefaultConstants.DefaultSubPanelThickness, 
                Style = CoreLineStyle.Solid, 
                IsOverlay = false,
                UpColor = IndicatorDefaultConstants.MacdHistogramUpColor,
                DownColor = IndicatorDefaultConstants.MacdHistogramDownColor,
                SeriesColors = new System.Collections.ObjectModel.ObservableCollection<SeriesColorConfig> 
                {
                    new SeriesColorConfig { Name = "MACD", DisplayName = "MACD Line", Color = IndicatorDefaultConstants.MacdLineColor, TargetSeries = new List<string> { "Main" } },
                    new SeriesColorConfig { Name = "Signal", DisplayName = "Signal Line", Color = IndicatorDefaultConstants.MacdSignalColor, TargetSeries = new List<string> { "Signal" } },
                    new SeriesColorConfig { Name = "Histogram", DisplayName = "Histogram", Color = IndicatorDefaultConstants.MacdHistogramColor, TargetSeries = new List<string> { "Histogram" } }
                }
            },
            new CoreIndicatorSettings 
            { 
                TypeEnum = IndicatorType.Stoch, 
                Category = CoreIndicatorCategory.Oscillator, 
                IsEnabled = false, 
                ParameterObject = new CoreStochasticParameter(), 
                Color = IndicatorDefaultConstants.StochBaseColor, 
                Thickness = IndicatorDefaultConstants.DefaultSubPanelThickness, 
                Style = CoreLineStyle.Solid, 
                IsOverlay = false,
                MinValue = IndicatorDefaultConstants.StochMinValue,
                MaxValue = IndicatorDefaultConstants.StochMaxValue,
                SeriesColors = new System.Collections.ObjectModel.ObservableCollection<SeriesColorConfig> 
                {
                    new SeriesColorConfig { Name = "K", DisplayName = "%K", Color = IndicatorDefaultConstants.StochKColor, TargetSeries = new List<string> { "Main", "Slow %K" } },
                    new SeriesColorConfig { Name = "D", DisplayName = "%D", Color = IndicatorDefaultConstants.StochDColor, TargetSeries = new List<string> { "PercentD", "Slow %D" } }
                }
            },
            new CoreIndicatorSettings 
            { 
                TypeEnum = IndicatorType.VolumeProfile, 
                Category = CoreIndicatorCategory.Chart, 
                IsEnabled = false, 
                ParameterObject = new CoreVolumeProfileParameter { Period = IndicatorDefaultConstants.VolumeProfilePeriod, RowCount = IndicatorDefaultConstants.VolumeProfileRowCount, Opacity = IndicatorDefaultConstants.VolumeProfileOpacity, Side = DisplaySide.Left, Mode = VolumeDistributionMode.Proportional }, 
                Color = IndicatorDefaultConstants.VolumeProfileColor, 
                Thickness = IndicatorDefaultConstants.DefaultBandThickness, 
                Style = CoreLineStyle.Solid,
                IsOverlay = true 
            },
            new CoreIndicatorSettings 
            { 
                TypeEnum = IndicatorType.TimeAtPrice, 
                Category = CoreIndicatorCategory.Chart, 
                IsEnabled = false, 
                ParameterObject = new CoreTimeAtPriceParameter { Period = IndicatorDefaultConstants.VolumeProfilePeriod, RowCount = IndicatorDefaultConstants.VolumeProfileRowCount, PriceType = PriceType.Close, Opacity = IndicatorDefaultConstants.VolumeProfileOpacity, Side = DisplaySide.Left }, 
                Color = IndicatorDefaultConstants.VolumeProfileColor, 
                Thickness = IndicatorDefaultConstants.DefaultBandThickness, 
                Style = CoreLineStyle.Solid,
                IsOverlay = true 
            },
            new CoreIndicatorSettings 
            { 
                TypeEnum = IndicatorType.MESA, 
                Category = CoreIndicatorCategory.Trend, 
                IsEnabled = false, 
                ParameterObject = new CoreMesaParameter(), 
                Color = IndicatorDefaultConstants.MesaBaseColor, 
                Thickness = IndicatorDefaultConstants.DefaultOverlayThickness, 
                Style = CoreLineStyle.Solid,
                IsOverlay = true,
                SeriesColors = new System.Collections.ObjectModel.ObservableCollection<SeriesColorConfig> 
                {
                    new SeriesColorConfig { Name = "MAMA", DisplayName = "MAMA", Color = IndicatorDefaultConstants.MesaMamaColor, TargetSeries = new List<string> { "Main" } },
                    new SeriesColorConfig { Name = "FAMA", DisplayName = "FAMA", Color = IndicatorDefaultConstants.MesaFamaColor, TargetSeries = new List<string> { "Fama" } }
                }
            },
            new CoreIndicatorSettings 
            { 
                TypeEnum = IndicatorType.Volume, 
                Category = CoreIndicatorCategory.Volume, 
                IsEnabled = false, 
                ParameterObject = null, 
                Color = IndicatorDefaultConstants.Gray, // Gray fallback
                Thickness = IndicatorDefaultConstants.DefaultBandThickness, 
                Style = CoreLineStyle.Solid,
                IsOverlay = false,
                UseUpDownColors = true,
                UpColor = IndicatorDefaultConstants.VolumeUpColor,
                DownColor = IndicatorDefaultConstants.VolumeDownColor
            },
            new CoreIndicatorSettings 
            { 
                TypeEnum = IndicatorType.PVT, 
                Category = CoreIndicatorCategory.Volume, 
                IsEnabled = false, 
                Color = IndicatorDefaultConstants.Purple, // Purple #AB47BC
                Thickness = IndicatorDefaultConstants.DefaultSubPanelThickness, 
                Style = CoreLineStyle.Solid, 
                IsOverlay = false
            },
            new CoreIndicatorSettings 
            { 
                TypeEnum = IndicatorType.VPT, 
                Category = CoreIndicatorCategory.Volume, 
                IsEnabled = false, 
                Color = IndicatorDefaultConstants.Purple, // Purple #AB47BC
                Thickness = IndicatorDefaultConstants.DefaultSubPanelThickness, 
                Style = CoreLineStyle.Solid, 
                IsOverlay = false
            },
            new CoreIndicatorSettings 
            { 
                TypeEnum = IndicatorType.OBV, 
                Category = CoreIndicatorCategory.Volume, 
                IsEnabled = false, 
                Color = IndicatorDefaultConstants.Cyan, // Cyan #00ACC1
                Thickness = IndicatorDefaultConstants.DefaultSubPanelThickness, 
                Style = CoreLineStyle.Solid, 
                IsOverlay = false
            },
            new CoreIndicatorSettings
            {
                TypeEnum = IndicatorType.VWAP,
                Category = CoreIndicatorCategory.Volume,
                IsEnabled = false,
                ParameterObject = new CoreVwapParameter(),
                PriceSource = PriceType.Typical,
                Color = IndicatorDefaultConstants.Cyan, // Cyan #00ACC1
                Thickness = IndicatorDefaultConstants.DefaultOverlayThickness,
                Style = CoreLineStyle.Solid,
                IsOverlay = true,
                SeriesColors = new System.Collections.ObjectModel.ObservableCollection<SeriesColorConfig>
                {
                    new SeriesColorConfig { Name = IndicatorResult.MainSeriesName, DisplayName = "VWAP", Color = IndicatorDefaultConstants.Cyan, TargetSeries = new List<string> { IndicatorResult.MainSeriesName } },
                    new SeriesColorConfig { Name = CoreVwapIndicator.UpperBandSeriesName, DisplayName = "Upper Band", Color = IndicatorDefaultConstants.BollingerBandsColor, TargetSeries = new List<string> { CoreVwapIndicator.UpperBandSeriesName } },
                    new SeriesColorConfig { Name = CoreVwapIndicator.LowerBandSeriesName, DisplayName = "Lower Band", Color = IndicatorDefaultConstants.BollingerBandsColor, TargetSeries = new List<string> { CoreVwapIndicator.LowerBandSeriesName } }
                }
            },
            new CoreIndicatorSettings
            {
                TypeEnum = IndicatorType.AnchoredVWAP,
                Category = CoreIndicatorCategory.Volume,
                IsEnabled = false,
                ParameterObject = new CoreAnchoredVwapParameter(),
                Color = IndicatorDefaultConstants.Cyan, // Cyan #00ACC1
                Thickness = IndicatorDefaultConstants.DefaultOverlayThickness,
                Style = CoreLineStyle.Solid,
                IsOverlay = true,
                SeriesColors = new System.Collections.ObjectModel.ObservableCollection<SeriesColorConfig>
                {
                    new SeriesColorConfig { Name = IndicatorResult.MainSeriesName, DisplayName = "Anchored VWAP", Color = IndicatorDefaultConstants.Cyan, TargetSeries = new List<string> { IndicatorResult.MainSeriesName } },
                    new SeriesColorConfig { Name = CoreAnchoredVWAPIndicator.UpperBandSeriesName, DisplayName = "Upper Band", Color = IndicatorDefaultConstants.BollingerBandsColor, TargetSeries = new List<string> { CoreAnchoredVWAPIndicator.UpperBandSeriesName } },
                    new SeriesColorConfig { Name = CoreAnchoredVWAPIndicator.LowerBandSeriesName, DisplayName = "Lower Band", Color = IndicatorDefaultConstants.BollingerBandsColor, TargetSeries = new List<string> { CoreAnchoredVWAPIndicator.LowerBandSeriesName } }
                }
            },
            new CoreIndicatorSettings
            {
                TypeEnum = IndicatorType.MarketStructureShift,
                Category = CoreIndicatorCategory.Trend, 
                IsEnabled = false, 
                ParameterObject = new CoreMarketStructureShiftParameter(), 
                Color = IndicatorDefaultConstants.SmaColor, 
                Thickness = IndicatorDefaultConstants.DefaultOverlayThickness, 
                Style = CoreLineStyle.Dash,
                IsOverlay = true,
                SeriesColors = new System.Collections.ObjectModel.ObservableCollection<SeriesColorConfig> 
                {
                    new SeriesColorConfig { Name = "BOS", DisplayName = "BOS Lines", Color = IndicatorDefaultConstants.LightBlue, TargetSeries = new List<string> { "BOS_Lines" } },
                    new SeriesColorConfig { Name = "CHoCH", DisplayName = "CHoCH Lines", Color = IndicatorDefaultConstants.Orange, TargetSeries = new List<string> { "CHoCH_Lines" } }
                }
            },
            new CoreIndicatorSettings 
            { 
                TypeEnum = IndicatorType.StructuralDtw, 
                Category = CoreIndicatorCategory.Oscillator, 
                IsEnabled = false, 
                ParameterObject = new CoreStructuralDtwParameter(), 
                Color = IndicatorDefaultConstants.StructuralDtwColor, 
                Style = CoreLineStyle.Solid, 
                IsOverlay = false
            },
            new CoreIndicatorSettings 
            { 
                TypeEnum = IndicatorType.GranvilleLaw, 
                Category = CoreIndicatorCategory.Trend, 
                IsEnabled = false, 
                ParameterObject = new CoreGranvilleLawParameter(), 
                Color = IndicatorDefaultConstants.GranvilleBuy1Color, 
                Thickness = IndicatorDefaultConstants.DefaultOverlayThickness, 
                Style = CoreLineStyle.Solid,
                IsOverlay = false,
                MinValue = -4,
                MaxValue = 4,
                SeriesColors = new System.Collections.ObjectModel.ObservableCollection<SeriesColorConfig> 
                {
                    new SeriesColorConfig { Name = "Buy1", DisplayName = "Buy 1 (Trend Reversal) : MA Flat/Up & Price crosses above MA", Color = IndicatorDefaultConstants.GranvilleBuy1Color, TargetSeries = new List<string> { "Signals" } },
                    new SeriesColorConfig { Name = "Buy2", DisplayName = "Buy 2 (Trend Continuation) : MA Up & Price drops below MA temporarily", Color = IndicatorDefaultConstants.GranvilleBuy2Color, TargetSeries = new List<string> { "Signals" } },
                    new SeriesColorConfig { Name = "Buy3", DisplayName = "Buy 3 (Support Bounce) : MA Up & Price bounces off MA", Color = IndicatorDefaultConstants.GranvilleBuy3Color, TargetSeries = new List<string> { "Signals" } },
                    new SeriesColorConfig { Name = "Buy4", DisplayName = "Buy 4 (Mean Reversion) : MA Down & Price deviates far below MA", Color = IndicatorDefaultConstants.GranvilleBuy4Color, TargetSeries = new List<string> { "Signals" } },
                    new SeriesColorConfig { Name = "Sell1", DisplayName = "Sell 1 (Trend Reversal) : MA Flat/Down & Price crosses below MA", Color = IndicatorDefaultConstants.GranvilleSell1Color, TargetSeries = new List<string> { "Signals" } },
                    new SeriesColorConfig { Name = "Sell2", DisplayName = "Sell 2 (Trend Continuation) : MA Down & Price rises above MA temporarily", Color = IndicatorDefaultConstants.GranvilleSell2Color, TargetSeries = new List<string> { "Signals" } },
                    new SeriesColorConfig { Name = "Sell3", DisplayName = "Sell 3 (Resistance Rejection) : MA Down & Price rejected at MA", Color = IndicatorDefaultConstants.GranvilleSell3Color, TargetSeries = new List<string> { "Signals" } },
                    new SeriesColorConfig { Name = "Sell4", DisplayName = "Sell 4 (Mean Reversion) : MA Up & Price deviates far above MA", Color = IndicatorDefaultConstants.GranvilleSell4Color, TargetSeries = new List<string> { "Signals" } }
                }
            },
            new CoreIndicatorSettings 
            { 
                TypeEnum = IndicatorType.WMA, 
                Category = CoreIndicatorCategory.Trend, 
                IsEnabled = false, 
                ParameterObject = new CoreSmaParameter { Period = IndicatorDefaultConstants.SmaPeriod }, 
                Color = IndicatorDefaultConstants.SmaColor, 
                Thickness = IndicatorDefaultConstants.DefaultOverlayThickness, 
                Style = CoreLineStyle.Solid 
            },
            new CoreIndicatorSettings 
            { 
                TypeEnum = IndicatorType.HMA, 
                Category = CoreIndicatorCategory.Trend, 
                IsEnabled = false, 
                ParameterObject = new CoreSmaParameter { Period = IndicatorDefaultConstants.SmaPeriod }, 
                Color = IndicatorDefaultConstants.SmaColor, 
                Thickness = IndicatorDefaultConstants.DefaultOverlayThickness, 
                Style = CoreLineStyle.Solid 
            },
            new CoreIndicatorSettings 
            { 
                TypeEnum = IndicatorType.TMA, 
                Category = CoreIndicatorCategory.Trend, 
                IsEnabled = false, 
                ParameterObject = new CoreSmaParameter { Period = IndicatorDefaultConstants.SmaPeriod }, 
                Color = IndicatorDefaultConstants.SmaColor, 
                Thickness = IndicatorDefaultConstants.DefaultOverlayThickness, 
                Style = CoreLineStyle.Solid 
            },
            new CoreIndicatorSettings 
            { 
                TypeEnum = IndicatorType.DEMA, 
                Category = CoreIndicatorCategory.Trend, 
                IsEnabled = false, 
                ParameterObject = new CoreSmaParameter { Period = IndicatorDefaultConstants.SmaPeriod }, 
                Color = IndicatorDefaultConstants.SmaColor, 
                Thickness = IndicatorDefaultConstants.DefaultOverlayThickness, 
                Style = CoreLineStyle.Solid 
            },
            new CoreIndicatorSettings 
            { 
                TypeEnum = IndicatorType.TEMA, 
                Category = CoreIndicatorCategory.Trend, 
                IsEnabled = false, 
                ParameterObject = new CoreSmaParameter { Period = IndicatorDefaultConstants.SmaPeriod }, 
                Color = IndicatorDefaultConstants.SmaColor, 
                Thickness = IndicatorDefaultConstants.DefaultOverlayThickness, 
                Style = CoreLineStyle.Solid,
                IsOverlay = true
            },
            new CoreIndicatorSettings 
            { 
                TypeEnum = IndicatorType.VWMA, 
                Category = CoreIndicatorCategory.Volume, 
                IsEnabled = false, 
                ParameterObject = new CoreSmaParameter { Period = IndicatorDefaultConstants.SmaPeriod }, 
                Color = IndicatorDefaultConstants.SmaColor, 
                Thickness = IndicatorDefaultConstants.DefaultOverlayThickness, 
                Style = CoreLineStyle.Solid,
                IsOverlay = true
            },
             new CoreIndicatorSettings 
            { 
                TypeEnum = IndicatorType.VAMA, 
                Category = CoreIndicatorCategory.Trend, 
                IsEnabled = false, 
                ParameterObject = new CoreSmaParameter { Period = IndicatorDefaultConstants.SmaPeriod }, 
                Color = IndicatorDefaultConstants.SmaColor, 
                Thickness = IndicatorDefaultConstants.DefaultOverlayThickness, 
                Style = CoreLineStyle.Solid 
            },
            new CoreIndicatorSettings 
            { 
                TypeEnum = IndicatorType.SMMA, 
                Category = CoreIndicatorCategory.Trend, 
                IsEnabled = false, 
                ParameterObject = new CoreSmaParameter { Period = IndicatorDefaultConstants.SmaPeriod }, 
                Color = IndicatorDefaultConstants.SmaColor, 
                Thickness = IndicatorDefaultConstants.DefaultOverlayThickness, 
                Style = CoreLineStyle.Solid 
            },
            new CoreIndicatorSettings 
            { 
                TypeEnum = IndicatorType.ZLEMA, 
                Category = CoreIndicatorCategory.Trend, 
                IsEnabled = false, 
                ParameterObject = new CoreSmaParameter { Period = IndicatorDefaultConstants.SmaPeriod }, 
                Color = IndicatorDefaultConstants.SmaColor, 
                Thickness = IndicatorDefaultConstants.DefaultOverlayThickness, 
                Style = CoreLineStyle.Solid 
            },
            new CoreIndicatorSettings 
            { 
                TypeEnum = IndicatorType.LSMA, 
                Category = CoreIndicatorCategory.Trend, 
                IsEnabled = false, 
                ParameterObject = new CoreSmaParameter { Period = IndicatorDefaultConstants.SmaPeriod }, 
                Color = IndicatorDefaultConstants.SmaColor, 
                Thickness = IndicatorDefaultConstants.DefaultOverlayThickness, 
                Style = CoreLineStyle.Solid 
            },
            new CoreIndicatorSettings 
            { 
                TypeEnum = IndicatorType.ALMA, 
                Category = CoreIndicatorCategory.Trend, 
                IsEnabled = false, 
                ParameterObject = new CoreAlmaParameter(), 
                Color = IndicatorDefaultConstants.SmaColor, 
                Thickness = IndicatorDefaultConstants.DefaultOverlayThickness, 
                Style = CoreLineStyle.Solid 
            },
            new CoreIndicatorSettings 
            { 
                TypeEnum = IndicatorType.MtfLaggedEma, 
                Category = CoreIndicatorCategory.Trend, 
                IsEnabled = false, 
                ParameterObject = new CoreMtfLaggedEmaParameter(), 
                Color = IndicatorDefaultConstants.EmaColor, 
                Thickness = IndicatorDefaultConstants.DefaultOverlayThickness, 
                Style = CoreLineStyle.Solid,
                IsOverlay = true
            },
            new CoreIndicatorSettings 
            { 
                TypeEnum = IndicatorType.DMI, 
                Category = CoreIndicatorCategory.Trend, 
                IsEnabled = false, 
                ParameterObject = new CoreDmiParameter { Period = IndicatorDefaultConstants.DmiDefaultPeriod }, 
                Color = IndicatorDefaultConstants.DmiPlusDiColor, 
                Thickness = IndicatorDefaultConstants.DefaultSubPanelThickness, 
                Style = CoreLineStyle.Solid,
                IsOverlay = false,
                SeriesColors = new System.Collections.ObjectModel.ObservableCollection<SeriesColorConfig> 
                {
                    new SeriesColorConfig { Name = "PlusDI", DisplayName = "+DI", Color = IndicatorDefaultConstants.DmiPlusDiColor, TargetSeries = new List<string> { "PlusDI", "Main" } },
                    new SeriesColorConfig { Name = "MinusDI", DisplayName = "-DI", Color = IndicatorDefaultConstants.DmiMinusDiColor, TargetSeries = new List<string> { "MinusDI" } }
                }
            },
            new CoreIndicatorSettings 
            { 
                TypeEnum = IndicatorType.ATR, 
                Category = CoreIndicatorCategory.Volatility, 
                IsEnabled = false, 
                ParameterObject = new CoreAtrParameter(), 
                Color = IndicatorDefaultConstants.AtrColor, 
                Thickness = IndicatorDefaultConstants.DefaultSubPanelThickness, 
                Style = CoreLineStyle.Solid,
                IsOverlay = false
            },
            new CoreIndicatorSettings 
            { 
                TypeEnum = IndicatorType.ROC, 
                Category = CoreIndicatorCategory.Oscillator, 
                IsEnabled = false, 
                ParameterObject = new CoreRocParameter(), 
                Color = IndicatorDefaultConstants.RocColor, 
                Thickness = IndicatorDefaultConstants.DefaultSubPanelThickness, 
                Style = CoreLineStyle.Solid,
                IsOverlay = false
            },
            new CoreIndicatorSettings
            {
                TypeEnum = IndicatorType.Highest,
                Category = CoreIndicatorCategory.Trend,
                IsEnabled = false,
                ParameterObject = new CoreSmaParameter { Period = 20 },
                PriceSource = PriceType.High,
                Color = IndicatorDefaultConstants.DeepPurple,
                Thickness = IndicatorDefaultConstants.DefaultBandThickness,
                Style = CoreLineStyle.Solid,
                IsOverlay = true
            },
            new CoreIndicatorSettings
            {
                TypeEnum = IndicatorType.Lowest,
                Category = CoreIndicatorCategory.Trend,
                IsEnabled = false,
                ParameterObject = new CoreSmaParameter { Period = 20 },
                PriceSource = PriceType.Low,
                Color = IndicatorDefaultConstants.Blue,
                Thickness = IndicatorDefaultConstants.DefaultBandThickness,
                Style = CoreLineStyle.Solid,
                IsOverlay = true
            },
            new CoreIndicatorSettings
            {
                TypeEnum = IndicatorType.TrueHigh,
                Category = CoreIndicatorCategory.Volatility,
                IsEnabled = false,
                ParameterObject = null,
                PriceSource = PriceType.TrueHigh,
                Color = IndicatorDefaultConstants.DeepPurple,
                Thickness = IndicatorDefaultConstants.DefaultBandThickness,
                Style = CoreLineStyle.Solid,
                IsOverlay = true
            },
            new CoreIndicatorSettings
            {
                TypeEnum = IndicatorType.TrueLow,
                Category = CoreIndicatorCategory.Volatility,
                IsEnabled = false,
                ParameterObject = null,
                PriceSource = PriceType.TrueLow,
                Color = IndicatorDefaultConstants.Blue,
                Thickness = IndicatorDefaultConstants.DefaultBandThickness,
                Style = CoreLineStyle.Solid,
                IsOverlay = true
            },
            new CoreIndicatorSettings
            {
                TypeEnum = IndicatorType.TrueRange,
                Category = CoreIndicatorCategory.Volatility,
                IsEnabled = false,
                ParameterObject = null,
                Color = IndicatorDefaultConstants.AtrColor,
                Thickness = IndicatorDefaultConstants.DefaultSubPanelThickness,
                Style = CoreLineStyle.Solid,
                IsOverlay = false
            },
            new CoreIndicatorSettings
            {
                TypeEnum = IndicatorType.DPO,
                Category = CoreIndicatorCategory.Oscillator, 
                IsEnabled = false, 
                ParameterObject = new CoreSmaParameter { Period = 20 }, 
                Color = IndicatorDefaultConstants.Purple, // Purple #AB47BC
                Thickness = IndicatorDefaultConstants.DefaultSubPanelThickness, 
                Style = CoreLineStyle.Solid,
                IsOverlay = false
            },
            new CoreIndicatorSettings 
            { 
                TypeEnum = IndicatorType.DeMarker, 
                Category = CoreIndicatorCategory.Oscillator, 
                IsEnabled = false, 
                ParameterObject = new CoreSmaParameter { Period = 14 }, 
                Color = IndicatorDefaultConstants.Purple, // Purple #AB47BC
                Thickness = IndicatorDefaultConstants.DefaultSubPanelThickness, 
                Style = CoreLineStyle.Solid,
                IsOverlay = false
            },
            new CoreIndicatorSettings 
            { 
                TypeEnum = IndicatorType.PrimeNumberOscillator, 
                Category = CoreIndicatorCategory.Oscillator, 
                IsEnabled = false, 
                ParameterObject = new CorePrimeNumberOscillatorParameter(), 
                Color = IndicatorDefaultConstants.RsiColor, // Purple #AB47BC
                Thickness = IndicatorDefaultConstants.DefaultSubPanelThickness, 
                Style = CoreLineStyle.Solid, 
                IsOverlay = false 
            },
            new CoreIndicatorSettings 
            { 
                TypeEnum = IndicatorType.PrimeNumberBands, 
                Category = CoreIndicatorCategory.Trend, 
                IsEnabled = false, 
                ParameterObject = new CorePrimeNumberBandsParameter 
                { 
                    Period = IndicatorDefaultConstants.PrimeNumberBandsPeriod, 
                    ScaleMultiplier = IndicatorDefaultConstants.PrimeNumberBandsScaleMultiplier 
                }, 
                Color = IndicatorDefaultConstants.PrimeNumberBandsMiddleColor, 
                Thickness = IndicatorDefaultConstants.DefaultBandThickness, 
                Style = CoreLineStyle.Solid,
                IsOverlay = true,
                SeriesColors = new System.Collections.ObjectModel.ObservableCollection<SeriesColorConfig>
                {
                    new SeriesColorConfig { Name = "Middle", DisplayName = "Middle Band", Color = IndicatorDefaultConstants.PrimeNumberBandsMiddleColor, TargetSeries = new List<string> { "Main" } },
                    new SeriesColorConfig { Name = "Bands", DisplayName = "Bands (Upper/Lower)", Color = IndicatorDefaultConstants.PrimeNumberBandsBandsColor, TargetSeries = new List<string> { "Upper", "Lower" } }
                }
            },
            new CoreIndicatorSettings
            {
                TypeEnum = IndicatorType.FFTCycle,
                Category = CoreIndicatorCategory.Other,
                IsEnabled = false,
                ParameterObject = new CoreFFTCycleParameter(),
                Color = IndicatorDefaultConstants.FftCycleColor,
                Thickness = IndicatorDefaultConstants.DefaultSubPanelThickness,
                Style = CoreLineStyle.Solid,
                IsOverlay = false,
                SeriesColors = new System.Collections.ObjectModel.ObservableCollection<SeriesColorConfig>
                {
                    new SeriesColorConfig { Name = "Period", DisplayName = "FFT Cycle", Color = IndicatorDefaultConstants.FftCycleColor, TargetSeries = new List<string> { "Main" } },
                    new SeriesColorConfig { Name = "Strength", DisplayName = "FFT Cycle Strength", Color = IndicatorDefaultConstants.FftCycleStrengthColor, TargetSeries = new List<string> { "CycleStrength" } },
                    new SeriesColorConfig { Name = "Oscillator", DisplayName = "FFT Cycle Oscillator", Color = IndicatorDefaultConstants.FftCycleOscillatorColor, TargetSeries = new List<string> { "Oscillator" } }
                }
            },
            new CoreIndicatorSettings
            {
                TypeEnum = IndicatorType.IFFTInstantaneousPhase,
                Category = CoreIndicatorCategory.Other,
                IsEnabled = false,
                ParameterObject = new CoreIfftInstantaneousPhaseParameter(),
                Color = IndicatorDefaultConstants.IfftInstantaneousPhaseColor,
                Thickness = IndicatorDefaultConstants.DefaultSubPanelThickness,
                Style = CoreLineStyle.Solid,
                IsOverlay = false,
                // SineWave/LeadSine are sin()-bounded to exactly [-1, +1]; fix the panel's Y axis
                // to that range instead of auto-scaling, so it never drifts based on visible data.
                MinValue = IndicatorDefaultConstants.IfftInstantaneousPhaseSineMinValue,
                MaxValue = IndicatorDefaultConstants.IfftInstantaneousPhaseSineMaxValue,
                SeriesColors = new System.Collections.ObjectModel.ObservableCollection<SeriesColorConfig>
                {
                    // "Phase" (Main, degrees) is intentionally NOT listed here: it is not drawn
                    // (see IndicatorRenderer's IFFTInstantaneousPhase/MainSeriesName skip) because
                    // its 0-360 scale would dominate the sub-panel's Y axis and hide
                    // SineWave/LeadSine (-1..1). The underlying value is still computed and served
                    // as the "Main" series for the screener; only its chart display is suppressed.
                    new SeriesColorConfig { Name = "SineWave", DisplayName = "Sine Wave", Color = IndicatorDefaultConstants.IfftInstantaneousPhaseSineColor, TargetSeries = new List<string> { "SineWave" } },
                    new SeriesColorConfig { Name = "LeadSine", DisplayName = "Lead Sine", Color = IndicatorDefaultConstants.IfftInstantaneousPhaseLeadSineColor, TargetSeries = new List<string> { "LeadSine" } }
                }
            },
            new CoreIndicatorSettings
            {
                TypeEnum = IndicatorType.IFFTInstantaneousAmplitude,
                Category = CoreIndicatorCategory.Trend,
                IsEnabled = false,
                ParameterObject = new CoreIfftInstantaneousAmplitudeParameter(),
                Color = IndicatorDefaultConstants.IfftInstantaneousAmplitudeColor,
                Thickness = IndicatorDefaultConstants.DefaultOverlayThickness,
                Style = CoreLineStyle.Solid,
                IsOverlay = true,
                SeriesColors = new System.Collections.ObjectModel.ObservableCollection<SeriesColorConfig>
                {
                    new SeriesColorConfig { Name = "Amplitude", DisplayName = "IFFT Instantaneous Amplitude", Color = IndicatorDefaultConstants.IfftInstantaneousAmplitudeColor, TargetSeries = new List<string> { "Main" } }
                }
            },
            new CoreIndicatorSettings
            {
                TypeEnum = IndicatorType.PolarAmplitudeRatio,
                Category = CoreIndicatorCategory.Other,
                IsEnabled = false,
                ParameterObject = new CorePolarAmplitudeRatioParameter(),
                Color = IndicatorDefaultConstants.PolarAmplitudeRatioColor,
                Thickness = IndicatorDefaultConstants.DefaultSubPanelThickness,
                Style = CoreLineStyle.Solid,
                IsOverlay = false,
                SeriesColors = new System.Collections.ObjectModel.ObservableCollection<SeriesColorConfig>
                {
                    new SeriesColorConfig { Name = "AmplitudeRatio", DisplayName = "Polar Amplitude Ratio", Color = IndicatorDefaultConstants.PolarAmplitudeRatioColor, TargetSeries = new List<string> { "Main" } }
                }
            },
            new CoreIndicatorSettings
            {
                TypeEnum = IndicatorType.PolarPhase,
                Category = CoreIndicatorCategory.Other,
                IsEnabled = false,
                ParameterObject = new CorePolarPhaseParameter(),
                Color = IndicatorDefaultConstants.PolarPhaseColor,
                Thickness = IndicatorDefaultConstants.DefaultSubPanelThickness,
                Style = CoreLineStyle.Solid,
                IsOverlay = false,
                // NormalizedInPhase/NormalizedQuadrature are bounded to exactly [-1, +1]; fix the
                // panel's Y axis to that range instead of auto-scaling from visible data.
                MinValue = IndicatorDefaultConstants.PolarPhaseComponentMinValue,
                MaxValue = IndicatorDefaultConstants.PolarPhaseComponentMaxValue,
                SeriesColors = new System.Collections.ObjectModel.ObservableCollection<SeriesColorConfig>
                {
                    // "Phase" (Main, 0-360 deg) and "PhaseStability" (degrees) are intentionally NOT
                    // listed: they are not drawn (see IndicatorRenderer's PolarPhase/MainSeriesName
                    // skip) because their scale would dominate the sub-panel Y axis and hide the
                    // NormalizedInPhase/NormalizedQuadrature phasor (-1..1). The phase value is still
                    // computed and served as the "Main" series for the screener.
                    new SeriesColorConfig { Name = "NormalizedInPhase", DisplayName = "Normalized In-Phase", Color = IndicatorDefaultConstants.PolarPhaseNormalizedInPhaseColor, TargetSeries = new List<string> { "NormalizedInPhase" } },
                    new SeriesColorConfig { Name = "NormalizedQuadrature", DisplayName = "Normalized Quadrature", Color = IndicatorDefaultConstants.PolarPhaseNormalizedQuadratureColor, TargetSeries = new List<string> { "NormalizedQuadrature" } }
                }
            },
            new CoreIndicatorSettings
            {
                TypeEnum = IndicatorType.PolarCycleAngularFrequency,
                Category = CoreIndicatorCategory.Other,
                IsEnabled = false,
                ParameterObject = new CorePolarCycleAngularFrequencyParameter(),
                Color = IndicatorDefaultConstants.PolarCycleAngularFrequencyColor,
                Thickness = IndicatorDefaultConstants.DefaultSubPanelThickness,
                Style = CoreLineStyle.Solid,
                IsOverlay = false,
                SeriesColors = new System.Collections.ObjectModel.ObservableCollection<SeriesColorConfig>
                {
                    new SeriesColorConfig { Name = "CycleAngularFrequency", DisplayName = "Polar Cycle Angular Frequency", Color = IndicatorDefaultConstants.PolarCycleAngularFrequencyColor, TargetSeries = new List<string> { "Main" } },
                    new SeriesColorConfig { Name = "PhaseAngularVelocity", DisplayName = "Phase Angular Velocity", Color = IndicatorDefaultConstants.PolarPhaseAngularVelocityColor, TargetSeries = new List<string> { "PhaseAngularVelocity" } },
                    new SeriesColorConfig { Name = "PhaseFrequencyError", DisplayName = "Phase Frequency Error", Color = IndicatorDefaultConstants.PolarPhaseFrequencyErrorColor, TargetSeries = new List<string> { "PhaseFrequencyError" } }
                }
            },
            new CoreIndicatorSettings
            {
                TypeEnum = IndicatorType.IFFTBandPassFilter,
                Category = CoreIndicatorCategory.Other,
                IsEnabled = false,
                ParameterObject = new CoreIfftBandPassFilterParameter(),
                Color = IndicatorDefaultConstants.IfftBandPassFilterColor,
                Thickness = IndicatorDefaultConstants.DefaultSubPanelThickness,
                Style = CoreLineStyle.Solid,
                IsOverlay = false,
                SeriesColors = new System.Collections.ObjectModel.ObservableCollection<SeriesColorConfig>
                {
                    new SeriesColorConfig { Name = "Main", DisplayName = "IFFT Band-Pass Filter", Color = IndicatorDefaultConstants.IfftBandPassFilterColor, TargetSeries = new List<string> { "Main" } }
                }
            },
            new CoreIndicatorSettings
            {
                TypeEnum = IndicatorType.AdaptiveRSI,
                Category = CoreIndicatorCategory.Oscillator,
                IsEnabled = false,
                ParameterObject = new CoreAdaptiveRsiParameter(),
                Color = IndicatorDefaultConstants.AdaptiveRsiColor,
                Thickness = IndicatorDefaultConstants.DefaultSubPanelThickness,
                Style = CoreLineStyle.Solid,
                IsOverlay = false,
                MinValue = IndicatorDefaultConstants.RsiMinValue,
                MaxValue = IndicatorDefaultConstants.RsiMaxValue,
                SeriesColors = new System.Collections.ObjectModel.ObservableCollection<SeriesColorConfig>
                {
                    new SeriesColorConfig { Name = "Main", DisplayName = "Adaptive RSI", Color = IndicatorDefaultConstants.AdaptiveRsiColor, TargetSeries = new List<string> { "Main" } },
                    new SeriesColorConfig { Name = "DominantPeriod", DisplayName = "Dominant Period", Color = IndicatorDefaultConstants.AdaptiveRsiDominantPeriodColor, TargetSeries = new List<string> { "DominantPeriod" } }
                }
            },
            new CoreIndicatorSettings
            {
                TypeEnum = IndicatorType.HilbertTransform,
                Category = CoreIndicatorCategory.Other,
                IsEnabled = false,
                ParameterObject = new CoreHilbertTransformParameter(),
                Color = IndicatorDefaultConstants.HilbertTransformColor,
                Thickness = IndicatorDefaultConstants.DefaultSubPanelThickness,
                Style = CoreLineStyle.Solid,
                IsOverlay = false,
                SeriesColors = new System.Collections.ObjectModel.ObservableCollection<SeriesColorConfig>
                {
                    new SeriesColorConfig { Name = "DominantCycle", DisplayName = "Hilbert Dominant Cycle", Color = IndicatorDefaultConstants.HilbertTransformColor, TargetSeries = new List<string> { "Main" } },
                    new SeriesColorConfig { Name = "InPhase", DisplayName = "In-Phase (I)", Color = IndicatorDefaultConstants.HilbertTransformInPhaseColor, TargetSeries = new List<string> { "InPhase" } },
                    new SeriesColorConfig { Name = "Quadrature", DisplayName = "Quadrature (Q)", Color = IndicatorDefaultConstants.HilbertTransformQuadratureColor, TargetSeries = new List<string> { "Quadrature" } }
                }
            },
            new CoreIndicatorSettings
            {
                TypeEnum = IndicatorType.HilbertSine,
                Category = CoreIndicatorCategory.Oscillator,
                IsEnabled = false,
                ParameterObject = new CoreHilbertSineParameter(),
                Color = IndicatorDefaultConstants.HilbertSineColor,
                Thickness = IndicatorDefaultConstants.DefaultSubPanelThickness,
                Style = CoreLineStyle.Solid,
                IsOverlay = false,
                MinValue = IndicatorDefaultConstants.HilbertSineMinValue,
                MaxValue = IndicatorDefaultConstants.HilbertSineMaxValue,
                SeriesColors = new System.Collections.ObjectModel.ObservableCollection<SeriesColorConfig>
                {
                    new SeriesColorConfig { Name = "Sine", DisplayName = "Sine", Color = IndicatorDefaultConstants.HilbertSineColor, TargetSeries = new List<string> { "Sine", "Main" } },
                    new SeriesColorConfig { Name = "LeadSine", DisplayName = "Lead Sine", Color = IndicatorDefaultConstants.HilbertLeadSineColor, TargetSeries = new List<string> { "LeadSine" } }
                }
            },
            new CoreIndicatorSettings
            {
                TypeEnum = IndicatorType.HilbertTrendline,
                Category = CoreIndicatorCategory.Trend,
                IsEnabled = false,
                ParameterObject = new CoreHilbertTrendlineParameter(),
                Color = IndicatorDefaultConstants.HilbertTrendlineColor,
                Thickness = IndicatorDefaultConstants.DefaultOverlayThickness,
                Style = CoreLineStyle.Solid,
                IsOverlay = true,
                SeriesColors = new System.Collections.ObjectModel.ObservableCollection<SeriesColorConfig>
                {
                    new SeriesColorConfig { Name = "Trendline", DisplayName = "Trendline", Color = IndicatorDefaultConstants.HilbertTrendlineColor, TargetSeries = new List<string> { "Main" } }
                }
            },
            new CoreIndicatorSettings
            {
                TypeEnum = IndicatorType.HilbertTrendMode,
                Category = CoreIndicatorCategory.Other,
                IsEnabled = false,
                ParameterObject = new CoreHilbertTrendModeParameter(),
                Color = IndicatorDefaultConstants.HilbertTrendModeColor,
                Thickness = IndicatorDefaultConstants.DefaultSubPanelThickness,
                Style = CoreLineStyle.Solid,
                IsOverlay = false,
                MinValue = IndicatorDefaultConstants.HilbertTrendModeMinValue,
                MaxValue = IndicatorDefaultConstants.HilbertTrendModeMaxValue,
                SeriesColors = new System.Collections.ObjectModel.ObservableCollection<SeriesColorConfig>
                {
                    new SeriesColorConfig { Name = "Mode", DisplayName = "Trend Mode", Color = IndicatorDefaultConstants.HilbertTrendModeColor, TargetSeries = new List<string> { "Main" } }
                }
            },
            // Indicators whose Configure() accepts a dedicated multi-field parameter that is NOT resolvable
            // by the CoreXxxIndicator -> CoreXxxParameter naming convention. Without an explicit entry the
            // reflection fallback in CoreIndicatorBase.GetDefaultSettings() could only attach a single-field
            // CoreSmaParameter, which their Configure() ignores. Values below mirror each indicator's own
            // current field defaults to preserve observable behavior.
            new CoreIndicatorSettings
            {
                TypeEnum = IndicatorType.BandWidth,
                Category = CoreIndicatorCategory.Oscillator,
                IsEnabled = false,
                ParameterObject = new CoreBollingerBandsParameter { Period = IndicatorDefaultConstants.BandWidthPeriod, StdDevMultiplier = IndicatorDefaultConstants.BandWidthStdDevMultiplier },
                Color = IndicatorDefaultConstants.RsiColor,
                Thickness = IndicatorDefaultConstants.DefaultSubPanelThickness,
                Style = CoreLineStyle.Solid,
                IsOverlay = false
            },
            new CoreIndicatorSettings
            {
                TypeEnum = IndicatorType.BollingerBandsRatio,
                Category = CoreIndicatorCategory.Oscillator,
                IsEnabled = false,
                ParameterObject = new CoreBollingerBandsParameter { Period = IndicatorDefaultConstants.BollingerBandsRatioPeriod, StdDevMultiplier = IndicatorDefaultConstants.BollingerBandsRatioStdDevMultiplier },
                Color = IndicatorDefaultConstants.RsiColor,
                Thickness = IndicatorDefaultConstants.DefaultSubPanelThickness,
                Style = CoreLineStyle.Solid,
                IsOverlay = false
            },
            new CoreIndicatorSettings
            {
                TypeEnum = IndicatorType.PriceOscillator,
                Category = CoreIndicatorCategory.Oscillator,
                IsEnabled = false,
                ParameterObject = new CorePpoParameter { FastPeriod = IndicatorDefaultConstants.PriceOscillatorFastPeriod, SlowPeriod = IndicatorDefaultConstants.PriceOscillatorSlowPeriod },
                Color = IndicatorDefaultConstants.RsiColor,
                Thickness = IndicatorDefaultConstants.DefaultSubPanelThickness,
                Style = CoreLineStyle.Solid,
                IsOverlay = false
            },
            new CoreIndicatorSettings
            {
                TypeEnum = IndicatorType.VaR,
                Category = CoreIndicatorCategory.Volatility,
                IsEnabled = false,
                ParameterObject = new CoreCVarParameter { Period = IndicatorDefaultConstants.VarPeriod, ConfidenceLevel = IndicatorDefaultConstants.VarConfidenceLevel },
                Color = IndicatorDefaultConstants.RsiColor,
                Thickness = IndicatorDefaultConstants.DefaultSubPanelThickness,
                Style = CoreLineStyle.Solid,
                IsOverlay = false
            },
            new CoreIndicatorSettings
            {
                TypeEnum = IndicatorType.HiddenMarkovModel,
                Category = CoreIndicatorCategory.Math,
                IsEnabled = false,
                ParameterObject = new CoreHmmParameter
                {
                    States = IndicatorDefaultConstants.HmmStates,
                    Period = IndicatorDefaultConstants.HmmPeriod,
                    MaxIterations = IndicatorDefaultConstants.HmmMaxIterations,
                    Tolerance = IndicatorDefaultConstants.HmmTolerance
                },
                Color = IndicatorDefaultConstants.HmmColor,
                Thickness = IndicatorDefaultConstants.DefaultSubPanelThickness,
                Style = CoreLineStyle.Solid,
                IsOverlay = false,
                MinValue = IndicatorDefaultConstants.HmmMinValue,
                MaxValue = IndicatorDefaultConstants.HmmMaxValue
            },
            new CoreIndicatorSettings
            {
                TypeEnum = IndicatorType.Correlation,
                Category = CoreIndicatorCategory.Other,
                IsEnabled = false,
                ParameterObject = new CoreCorrelationParameter
                {
                    Period = IndicatorDefaultConstants.CorrelationPeriod
                },
                Color = IndicatorDefaultConstants.CorrelationColor,
                Thickness = IndicatorDefaultConstants.DefaultSubPanelThickness,
                Style = CoreLineStyle.Solid,
                IsOverlay = false,
                MinValue = IndicatorDefaultConstants.CorrelationMinValue,
                MaxValue = IndicatorDefaultConstants.CorrelationMaxValue
            },
            new CoreIndicatorSettings
            {
                TypeEnum = IndicatorType.FrechetOscillator,
                Category = CoreIndicatorCategory.Oscillator,
                IsEnabled = false,
                ParameterObject = new CoreFrechetOscillatorParameter
                {
                    Period = IndicatorDefaultConstants.FrechetOscillatorDefaultPeriod,
                    Lag = IndicatorDefaultConstants.FrechetOscillatorDefaultLag
                },
                Color = IndicatorDefaultConstants.FrechetOscillatorColor,
                Thickness = IndicatorDefaultConstants.DefaultSubPanelThickness,
                Style = CoreLineStyle.Solid,
                IsOverlay = false
            },
            new CoreIndicatorSettings
            {
                TypeEnum = IndicatorType.SSA,
                Category = CoreIndicatorCategory.Trend,
                IsEnabled = false,
                ParameterObject = new CoreSSAParameter
                {
                    WindowSize = IndicatorDefaultConstants.SsaDefaultWindowSize,
                    EmbeddingDimension = IndicatorDefaultConstants.SsaDefaultEmbeddingDimension,
                    NumComponents = IndicatorDefaultConstants.SsaDefaultNumComponents
                },
                Color = IndicatorDefaultConstants.SsaColor,
                Thickness = IndicatorDefaultConstants.DefaultOverlayThickness,
                Style = CoreLineStyle.Solid,
                IsOverlay = true
            },
            new CoreIndicatorSettings
            {
                TypeEnum = IndicatorType.SSAResidualBand,
                Category = CoreIndicatorCategory.Volatility,
                IsEnabled = false,
                ParameterObject = new CoreSSAResidualBandParameter
                {
                    WindowSize = IndicatorDefaultConstants.SsaResidualBandDefaultWindowSize,
                    EmbeddingDimension = IndicatorDefaultConstants.SsaResidualBandDefaultEmbeddingDimension,
                    NumComponents = IndicatorDefaultConstants.SsaResidualBandDefaultNumComponents,
                    Multiplier = IndicatorDefaultConstants.SsaResidualBandDefaultMultiplier
                },
                Color = IndicatorDefaultConstants.SsaResidualBandCenterColor,
                Thickness = IndicatorDefaultConstants.DefaultBandThickness,
                Style = CoreLineStyle.Solid,
                IsOverlay = true,
                SeriesColors = new System.Collections.ObjectModel.ObservableCollection<SeriesColorConfig>
                {
                    new SeriesColorConfig { Name = "Center", DisplayName = "Center Line", Color = IndicatorDefaultConstants.SsaResidualBandCenterColor, TargetSeries = new List<string> { "Main", "Center" } },
                    new SeriesColorConfig { Name = "Bands", DisplayName = "Bands (Upper/Lower)", Color = IndicatorDefaultConstants.SsaResidualBandBandsColor, TargetSeries = new List<string> { "Upper", "Lower" } }
                }
            },
            new CoreIndicatorSettings
            {
                TypeEnum = IndicatorType.SSACycle,
                Category = CoreIndicatorCategory.Oscillator,
                IsEnabled = false,
                ParameterObject = new CoreSSACycleParameter
                {
                    WindowSize = IndicatorDefaultConstants.SsaCycleDefaultWindowSize,
                    EmbeddingDimension = IndicatorDefaultConstants.SsaCycleDefaultEmbeddingDimension,
                    DeltaPair = IndicatorDefaultConstants.SsaCycleDefaultDeltaPair
                },
                Color = IndicatorDefaultConstants.SsaCycleColor,
                Thickness = IndicatorDefaultConstants.DefaultSubPanelThickness,
                Style = CoreLineStyle.Solid,
                IsOverlay = false,
                SeriesColors = new System.Collections.ObjectModel.ObservableCollection<SeriesColorConfig>
                {
                    new SeriesColorConfig { Name = "Cycle", DisplayName = "Dominant Cycle", Color = IndicatorDefaultConstants.SsaCycleColor, TargetSeries = new List<string> { "Main", "Cycle" } },
                    new SeriesColorConfig { Name = "InPhase", DisplayName = "In-Phase (I)", Color = IndicatorDefaultConstants.SsaCycleInPhaseColor, TargetSeries = new List<string> { "InPhase" } },
                    new SeriesColorConfig { Name = "Quadrature", DisplayName = "Quadrature (Q)", Color = IndicatorDefaultConstants.SsaCycleQuadratureColor, TargetSeries = new List<string> { "Quadrature" } },
                    new SeriesColorConfig { Name = "Phase", DisplayName = "Instantaneous Phase", Color = IndicatorDefaultConstants.SsaCyclePhaseColor, TargetSeries = new List<string> { "Phase" } }
                }
            },
            new CoreIndicatorSettings
            {
                TypeEnum = IndicatorType.SSAEntropy,
                Category = CoreIndicatorCategory.Oscillator,
                IsEnabled = false,
                ParameterObject = new CoreSSAEntropyParameter
                {
                    WindowSize = IndicatorDefaultConstants.SsaEntropyDefaultWindowSize,
                    EmbeddingDimension = IndicatorDefaultConstants.SsaEntropyDefaultEmbeddingDimension
                },
                Color = IndicatorDefaultConstants.SsaEntropyColor,
                Thickness = IndicatorDefaultConstants.DefaultSubPanelThickness,
                Style = CoreLineStyle.Solid,
                IsOverlay = false,
                MinValue = IndicatorDefaultConstants.SsaEntropyMinValue,
                MaxValue = IndicatorDefaultConstants.SsaEntropyMaxValue
            },
            new CoreIndicatorSettings
            {
                TypeEnum = IndicatorType.SSASqueeze,
                Category = CoreIndicatorCategory.Oscillator,
                IsEnabled = false,
                ParameterObject = new CoreSSASqueezeParameter
                {
                    WindowSize = IndicatorDefaultConstants.SsaSqueezeDefaultWindowSize,
                    EmbeddingDimension = IndicatorDefaultConstants.SsaSqueezeDefaultEmbeddingDimension,
                    NumComponents = IndicatorDefaultConstants.SsaSqueezeDefaultNumComponents,
                    SsaMultiplier = IndicatorDefaultConstants.SsaSqueezeDefaultSsaMultiplier,
                    AtrPeriod = IndicatorDefaultConstants.SsaSqueezeDefaultAtrPeriod,
                    AtrMultiplier = IndicatorDefaultConstants.SsaSqueezeDefaultAtrMultiplier,
                    MomentumPeriod = IndicatorDefaultConstants.SsaSqueezeDefaultMomentumPeriod,
                    SqueezeThreshold = IndicatorDefaultConstants.SsaSqueezeDefaultSqueezeThreshold
                },
                Color = IndicatorDefaultConstants.SsaSqueezeMomentumUpColor,
                Thickness = IndicatorDefaultConstants.DefaultSubPanelThickness,
                Style = CoreLineStyle.Solid,
                IsOverlay = false,
                SeriesColors = new System.Collections.ObjectModel.ObservableCollection<SeriesColorConfig>
                {
                    new SeriesColorConfig { Name = "Momentum", DisplayName = "Momentum Histogram", Color = IndicatorDefaultConstants.SsaSqueezeMomentumUpColor, TargetSeries = new List<string> { "Main", "Momentum" } },
                    new SeriesColorConfig { Name = "Squeeze", DisplayName = "Squeeze Status", Color = IndicatorDefaultConstants.SsaSqueezeOnColor, TargetSeries = new List<string> { "SqueezeStatus" } },
                    new SeriesColorConfig { Name = "Ratio", DisplayName = "Squeeze Ratio", Color = IndicatorDefaultConstants.Gray, TargetSeries = new List<string> { "SqueezeRatio" } }
                }
            },
            new CoreIndicatorSettings
            {
                TypeEnum = IndicatorType.SSASNR,
                Category = CoreIndicatorCategory.Oscillator,
                IsEnabled = false,
                ParameterObject = new CoreSSASNRParameter
                {
                    WindowSize = IndicatorDefaultConstants.SsaSnrDefaultWindowSize,
                    EmbeddingDimension = IndicatorDefaultConstants.SsaSnrDefaultEmbeddingDimension,
                    NumComponents = IndicatorDefaultConstants.SsaSnrDefaultNumComponents,
                    ThresholdHigh = IndicatorDefaultConstants.SsaSnrDefaultThresholdHigh,
                    ThresholdLow = IndicatorDefaultConstants.SsaSnrDefaultThresholdLow
                },
                Color = IndicatorDefaultConstants.SsaSnrColor,
                Thickness = IndicatorDefaultConstants.DefaultSubPanelThickness,
                Style = CoreLineStyle.Solid,
                IsOverlay = false,
                MinValue = -20.0m,
                MaxValue = 40.0m,
                SeriesColors = new System.Collections.ObjectModel.ObservableCollection<SeriesColorConfig>
                {
                    new SeriesColorConfig { Name = "SNR", DisplayName = "SNR (dB)", Color = IndicatorDefaultConstants.SsaSnrColor, TargetSeries = new List<string> { "Main", "SNR_dB" } },
                    new SeriesColorConfig { Name = "Purity", DisplayName = "Signal Purity (%)", Color = IndicatorDefaultConstants.SsaSnrPurityColor, TargetSeries = new List<string> { "SignalPurity" } },
                    new SeriesColorConfig { Name = "ThresholdHigh", DisplayName = "High Threshold", Color = IndicatorDefaultConstants.SsaSnrThresholdHighColor, TargetSeries = new List<string> { "ThresholdHigh" } },
                    new SeriesColorConfig { Name = "ThresholdLow", DisplayName = "Low Threshold", Color = IndicatorDefaultConstants.SsaSnrThresholdLowColor, TargetSeries = new List<string> { "ThresholdLow" } }
                }
            },
            new CoreIndicatorSettings
            {
                TypeEnum = IndicatorType.SSAAnomaly,
                Category = CoreIndicatorCategory.Oscillator,
                IsEnabled = false,
                ParameterObject = new CoreSSAAnomalyParameter
                {
                    WindowSize = IndicatorDefaultConstants.SsaAnomalyDefaultWindowSize,
                    EmbeddingDimension = IndicatorDefaultConstants.SsaAnomalyDefaultEmbeddingDimension,
                    NumComponents = IndicatorDefaultConstants.SsaAnomalyDefaultNumComponents,
                    AutoRank = IndicatorDefaultConstants.SsaAnomalyDefaultAutoRank,
                    EnterThreshold = IndicatorDefaultConstants.SsaAnomalyDefaultEnterThreshold,
                    ExitThreshold = IndicatorDefaultConstants.SsaAnomalyDefaultExitThreshold,
                    CoolDownPeriod = IndicatorDefaultConstants.SsaAnomalyDefaultCoolDownPeriod,
                    MinDuration = IndicatorDefaultConstants.SsaAnomalyDefaultMinDuration
                },
                Color = IndicatorDefaultConstants.SsaAnomalyZScoreColor,
                Thickness = IndicatorDefaultConstants.DefaultSubPanelThickness,
                Style = CoreLineStyle.Solid,
                IsOverlay = false,
                MinValue = -5.0m,
                MaxValue = 5.0m,
                SeriesColors = new System.Collections.ObjectModel.ObservableCollection<SeriesColorConfig>
                {
                    new SeriesColorConfig { Name = "ZScore", DisplayName = "Z-Score (σ)", Color = IndicatorDefaultConstants.SsaAnomalyZScoreColor, TargetSeries = new List<string> { "Main", "ZScore" } },
                    new SeriesColorConfig { Name = "AnomalyState", DisplayName = "Anomaly State", Color = IndicatorDefaultConstants.SsaAnomalyBullishColor, TargetSeries = new List<string> { "AnomalyState" } },
                    new SeriesColorConfig { Name = "EnterThreshold", DisplayName = "Enter Threshold", Color = IndicatorDefaultConstants.SsaAnomalyThresholdColor, TargetSeries = new List<string> { "EnterThreshold", "ThresholdEnter" } },
                    new SeriesColorConfig { Name = "ExitThreshold", DisplayName = "Exit Threshold", Color = IndicatorDefaultConstants.SsaAnomalyThresholdColor, TargetSeries = new List<string> { "ExitThreshold", "ThresholdExit" } }
                }
            },
            new CoreIndicatorSettings
            {
                TypeEnum = IndicatorType.ARIMA,
                Category = CoreIndicatorCategory.Math,
                IsEnabled = false,
                ParameterObject = new CoreArimaParameter
                {
                    P = IndicatorDefaultConstants.ArimaDefaultP,
                    D = IndicatorDefaultConstants.ArimaDefaultD,
                    Q = IndicatorDefaultConstants.ArimaDefaultQ,
                    Period = IndicatorDefaultConstants.ArimaDefaultPeriod
                },
                Color = IndicatorDefaultConstants.ArimaColor,
                Thickness = IndicatorDefaultConstants.DefaultOverlayThickness,
                Style = CoreLineStyle.Solid,
                IsOverlay = true
            },
            new CoreIndicatorSettings
            {
                TypeEnum = IndicatorType.Price,
                DisplayName = "Close",
                Category = CoreIndicatorCategory.Other,
                IsEnabled = false,
                ParameterObject = null,
                PriceSource = PriceType.Close,
                Color = IndicatorDefaultConstants.Orange,
                Thickness = IndicatorDefaultConstants.DefaultOverlayThickness,
                Style = CoreLineStyle.Solid,
                IsOverlay = true
            },
            new CoreIndicatorSettings
            {
                TypeEnum = IndicatorType.Autocorrelation,
                DisplayName = "Autocorrelation",
                Category = CoreIndicatorCategory.Oscillator,
                IsEnabled = false,
                ParameterObject = new CoreAutocorrelationParameter(),
                Color = IndicatorColor.FromUInt(0xFF42A5F5),
                Thickness = IndicatorDefaultConstants.DefaultSubPanelThickness,
                Style = CoreLineStyle.Solid,
                IsOverlay = false,
                MinValue = -1.0m,
                MaxValue = 1.0m
            },
            new CoreIndicatorSettings
            {
                TypeEnum = IndicatorType.EfficiencyRatio,
                DisplayName = "Kaufman Efficiency Ratio",
                Category = CoreIndicatorCategory.Oscillator,
                IsEnabled = false,
                ParameterObject = new CoreEfficiencyRatioParameter { Period = IndicatorDefaultConstants.EfficiencyRatioPeriod },
                Color = IndicatorDefaultConstants.EfficiencyRatioColor,
                Thickness = IndicatorDefaultConstants.DefaultSubPanelThickness,
                Style = CoreLineStyle.Solid,
                IsOverlay = false,
                MinValue = IndicatorDefaultConstants.EfficiencyRatioMinValue,
                MaxValue = IndicatorDefaultConstants.EfficiencyRatioMaxValue
            },
            new CoreIndicatorSettings
            {
                TypeEnum = IndicatorType.FDI,
                DisplayName = "Fractal Dimension Index",
                Category = CoreIndicatorCategory.Volatility,
                IsEnabled = false,
                ParameterObject = new CoreFDIParameter
                {
                    Period = IndicatorDefaultConstants.FdiPeriod,
                    SmoothingPeriod = IndicatorDefaultConstants.FdiSmoothingPeriod
                },
                Color = IndicatorDefaultConstants.FdiColor,
                Thickness = IndicatorDefaultConstants.DefaultSubPanelThickness,
                Style = CoreLineStyle.Solid,
                IsOverlay = false,
                MinValue = IndicatorDefaultConstants.FdiMinValue,
                MaxValue = IndicatorDefaultConstants.FdiMaxValue
            },
            new CoreIndicatorSettings 
            { 
                TypeEnum = IndicatorType.ClothoidOscillator, 
                Category = CoreIndicatorCategory.Oscillator, 
                IsEnabled = false, 
                ParameterObject = new CoreClothoidOscillatorParameter(), 
                Color = IndicatorDefaultConstants.ClothoidOscillatorColor, 
                Thickness = IndicatorDefaultConstants.DefaultSubPanelThickness, 
                Style = CoreLineStyle.Solid, 
                IsOverlay = false,
                MinValue = null,
                MaxValue = null,
                IncludeZeroBaseline = true,
                UpColor = IndicatorDefaultConstants.ClothoidOscillatorHistUpColor,
                DownColor = IndicatorDefaultConstants.ClothoidOscillatorHistDownColor,
                SeriesColors = new System.Collections.ObjectModel.ObservableCollection<SeriesColorConfig> 
                {
                    new SeriesColorConfig { Name = "ClothoidOscillator", DisplayName = "Curvature Rate", Color = IndicatorDefaultConstants.ClothoidOscillatorColor, TargetSeries = new List<string> { "Main" } },
                    new SeriesColorConfig { Name = "Signal", DisplayName = "Signal Line", Color = IndicatorDefaultConstants.ClothoidOscillatorSignalColor, TargetSeries = new List<string> { "Signal" } },
                    new SeriesColorConfig { Name = "Histogram", DisplayName = "Histogram", Color = IndicatorDefaultConstants.ClothoidOscillatorHistColor, TargetSeries = new List<string> { "Histogram" } }
                }
            },
            new CoreIndicatorSettings 
            { 
                TypeEnum = IndicatorType.ClothoidMovingAverage, 
                Category = CoreIndicatorCategory.Trend, 
                IsEnabled = false, 
                ParameterObject = new CoreClothoidMovingAverageParameter(), 
                Color = IndicatorDefaultConstants.ClothoidMovingAverageColor, 
                Thickness = IndicatorDefaultConstants.DefaultOverlayThickness, 
                Style = CoreLineStyle.Solid, 
                IsOverlay = true 
            },
            new CoreIndicatorSettings 
            { 
                TypeEnum = IndicatorType.CrossCorrelation, 
                Category = CoreIndicatorCategory.Other, 
                IsEnabled = false, 
                ParameterObject = new CoreCrossCorrelationParameter(), 
                Color = IndicatorDefaultConstants.CrossCorrelationColor, 
                Thickness = IndicatorDefaultConstants.DefaultSubPanelThickness, 
                Style = CoreLineStyle.Solid, 
                IsOverlay = false,
                MinValue = IndicatorDefaultConstants.CrossCorrelationMinValue,
                MaxValue = IndicatorDefaultConstants.CrossCorrelationMaxValue,
                IncludeZeroBaseline = true,
                SeriesColors = new System.Collections.ObjectModel.ObservableCollection<SeriesColorConfig>
                {
                    new SeriesColorConfig { Name = "CrossCorrelation", DisplayName = "Correlation", Color = IndicatorDefaultConstants.CrossCorrelationColor, TargetSeries = new List<string> { "Main" } }
                }
            },
            new CoreIndicatorSettings 
            { 
                TypeEnum = IndicatorType.RollingBeta, 
                Category = CoreIndicatorCategory.Other, 
                IsEnabled = false, 
                ParameterObject = new CoreRollingBetaParameter(), 
                Color = IndicatorDefaultConstants.RollingBetaColor, 
                Thickness = IndicatorDefaultConstants.DefaultSubPanelThickness, 
                Style = CoreLineStyle.Solid, 
                IsOverlay = false,
                IncludeZeroBaseline = true,
                SeriesColors = new System.Collections.ObjectModel.ObservableCollection<SeriesColorConfig>
                {
                    new SeriesColorConfig { Name = "Values", DisplayName = "Beta", Color = IndicatorDefaultConstants.RollingBetaColor, TargetSeries = new List<string> { "Main" } },
                    new SeriesColorConfig { Name = "RollingCorrelation", DisplayName = "Correlation", Color = IndicatorDefaultConstants.RollingBetaCorrelationColor, TargetSeries = new List<string> { "RollingCorrelation" } }
                }
            },
            new CoreIndicatorSettings 
            { 
                TypeEnum = IndicatorType.RollingAlpha, 
                Category = CoreIndicatorCategory.Other, 
                IsEnabled = false, 
                ParameterObject = new CoreRollingAlphaParameter(), 
                Color = IndicatorDefaultConstants.RollingAlphaColor, 
                Thickness = IndicatorDefaultConstants.DefaultSubPanelThickness, 
                Style = CoreLineStyle.Solid, 
                IsOverlay = false,
                IncludeZeroBaseline = true,
                SeriesColors = new System.Collections.ObjectModel.ObservableCollection<SeriesColorConfig>
                {
                    new SeriesColorConfig { Name = "Values", DisplayName = "Alpha", Color = IndicatorDefaultConstants.RollingAlphaColor, TargetSeries = new List<string> { "Main" } }
                }
            },
            new CoreIndicatorSettings 
            { 
                TypeEnum = IndicatorType.Ratiocator, 
                Category = CoreIndicatorCategory.Oscillator, 
                IsEnabled = false, 
                ParameterObject = new CoreRatiocatorParameter(), 
                Color = IndicatorDefaultConstants.RatiocatorColor, 
                Thickness = IndicatorDefaultConstants.DefaultSubPanelThickness, 
                Style = CoreLineStyle.Solid, 
                IsOverlay = false,
                SeriesColors = new System.Collections.ObjectModel.ObservableCollection<SeriesColorConfig>
                {
                    new SeriesColorConfig { Name = "Ratio", DisplayName = "Ratiocator", Color = IndicatorDefaultConstants.RatiocatorColor, TargetSeries = new List<string> { "Main" } },
                    new SeriesColorConfig { Name = "Signal", DisplayName = "Signal (EMA)", Color = IndicatorDefaultConstants.RatiocatorSignalColor, TargetSeries = new List<string> { "Signal" } }
                }
            },
            new CoreIndicatorSettings 
            { 
                TypeEnum = IndicatorType.RollingRSquared, 
                Category = CoreIndicatorCategory.Other, 
                IsEnabled = false, 
                ParameterObject = new CoreRollingRSquaredParameter(), 
                Color = IndicatorDefaultConstants.RollingRSquaredColor, 
                Thickness = IndicatorDefaultConstants.DefaultSubPanelThickness, 
                Style = CoreLineStyle.Solid, 
                IsOverlay = false,
                MinValue = IndicatorDefaultConstants.RollingRSquaredMinValue,
                MaxValue = IndicatorDefaultConstants.RollingRSquaredMaxValue,
                IncludeZeroBaseline = true,
                SeriesColors = new System.Collections.ObjectModel.ObservableCollection<SeriesColorConfig>
                {
                    new SeriesColorConfig { Name = "Values", DisplayName = "R²", Color = IndicatorDefaultConstants.RollingRSquaredColor, TargetSeries = new List<string> { "Main" } }
                }
            },
            new CoreIndicatorSettings 
            { 
                TypeEnum = IndicatorType.CointegrationSpread, 
                Category = CoreIndicatorCategory.Other, 
                IsEnabled = false, 
                ParameterObject = new CoreCointegrationSpreadParameter(), 
                Color = IndicatorDefaultConstants.CointegrationSpreadColor, 
                Thickness = IndicatorDefaultConstants.DefaultSubPanelThickness, 
                Style = CoreLineStyle.Solid, 
                IsOverlay = false,
                IncludeZeroBaseline = true,
                SeriesColors = new System.Collections.ObjectModel.ObservableCollection<SeriesColorConfig>
                {
                    new SeriesColorConfig { Name = "Values", DisplayName = "Spread", Color = IndicatorDefaultConstants.CointegrationSpreadColor, TargetSeries = new List<string> { "Main" } },
                    new SeriesColorConfig { Name = CoreCointegrationSpreadIndicator.ZScoreSeriesName, DisplayName = "Z-Score", Color = IndicatorDefaultConstants.CointegrationSpreadZScoreColor, TargetSeries = new List<string> { CoreCointegrationSpreadIndicator.ZScoreSeriesName } },
                    new SeriesColorConfig { Name = CoreCointegrationSpreadIndicator.HedgeRatioSeriesName, DisplayName = "Hedge Ratio (β)", Color = IndicatorDefaultConstants.CointegrationSpreadHedgeRatioColor, TargetSeries = new List<string> { CoreCointegrationSpreadIndicator.HedgeRatioSeriesName } },
                    new SeriesColorConfig { Name = CoreCointegrationSpreadIndicator.ADFStatisticSeriesName, DisplayName = "ADF Statistic (τ)", Color = IndicatorDefaultConstants.CointegrationSpreadAdfColor, TargetSeries = new List<string> { CoreCointegrationSpreadIndicator.ADFStatisticSeriesName } }
                }
            },
            new CoreIndicatorSettings
            {
                TypeEnum = IndicatorType.PairRatio,
                Category = CoreIndicatorCategory.Other,
                IsEnabled = false,
                ParameterObject = new CorePairRatioParameter(),
                Color = IndicatorDefaultConstants.PairRatioColor,
                Thickness = IndicatorDefaultConstants.DefaultSubPanelThickness,
                Style = CoreLineStyle.Solid,
                IsOverlay = false,
                SeriesColors = new System.Collections.ObjectModel.ObservableCollection<SeriesColorConfig>
                {
                    new SeriesColorConfig { Name = "Values", DisplayName = "Pair Ratio (A/B)", Color = IndicatorDefaultConstants.PairRatioColor, TargetSeries = new List<string> { "Main" } }
                }
            },
            new CoreIndicatorSettings
            {
                TypeEnum = IndicatorType.PairSpread,
                Category = CoreIndicatorCategory.Other,
                IsEnabled = false,
                ParameterObject = new CorePairSpreadParameter(),
                Color = IndicatorDefaultConstants.PairSpreadColor,
                Thickness = IndicatorDefaultConstants.DefaultSubPanelThickness,
                Style = CoreLineStyle.Solid,
                IsOverlay = false,
                IncludeZeroBaseline = true,
                SeriesColors = new System.Collections.ObjectModel.ObservableCollection<SeriesColorConfig>
                {
                    new SeriesColorConfig { Name = "Values", DisplayName = "Pair Spread (A-B)", Color = IndicatorDefaultConstants.PairSpreadColor, TargetSeries = new List<string> { "Main" } }
                }
            },
            new CoreIndicatorSettings
            {
                // Values equal what CoreIndicatorBase.GetDefaultSettings() returns for KST (pinned by a test); Signal colour = MACD signal colour (colour table U-01=A).
                TypeEnum = IndicatorType.KST,
                Category = CoreIndicatorCategory.Oscillator,
                IsEnabled = false,
                ParameterObject = new CoreKstParameter(),
                Color = IndicatorDefaultConstants.RsiColor,
                Thickness = IndicatorDefaultConstants.DefaultSubPanelThickness,
                Style = CoreLineStyle.Solid,
                IsOverlay = false,
                SeriesColors = new System.Collections.ObjectModel.ObservableCollection<SeriesColorConfig>
                {
                    new SeriesColorConfig { Name = IndicatorResult.MainSeriesName, DisplayName = "KST", Color = IndicatorDefaultConstants.RsiColor, TargetSeries = new List<string> { IndicatorResult.MainSeriesName } },
                    new SeriesColorConfig { Name = nameof(CoreKstIndicator.Signal), DisplayName = "Signal Line", Color = IndicatorDefaultConstants.MacdSignalColor, TargetSeries = new List<string> { nameof(CoreKstIndicator.Signal) } }
                }
            },
            new CoreIndicatorSettings
            {
                TypeEnum = IndicatorType.ClassicPivotPoints,
                Category = CoreIndicatorCategory.Chart,
                IsEnabled = false,
                ParameterObject = new CoreClassicPivotPointsParameter(),
                Color = IndicatorDefaultConstants.ClassicPivotPointsPivotColor, 
                Thickness = IndicatorDefaultConstants.DefaultBandThickness, 
                Style = CoreLineStyle.Solid, 
                IsOverlay = true,
                SeriesColors = new System.Collections.ObjectModel.ObservableCollection<SeriesColorConfig>
                {
                    new SeriesColorConfig { Name = "Pivot", DisplayName = "Pivot (P)", Color = IndicatorDefaultConstants.ClassicPivotPointsPivotColor, TargetSeries = new List<string> { "Main" } },
                    new SeriesColorConfig { Name = CoreClassicPivotPointsIndicator.R1SeriesName, DisplayName = "Resistance 1 (R1)", Color = IndicatorDefaultConstants.ClassicPivotPointsR1Color, TargetSeries = new List<string> { CoreClassicPivotPointsIndicator.R1SeriesName } },
                    new SeriesColorConfig { Name = CoreClassicPivotPointsIndicator.R2SeriesName, DisplayName = "Resistance 2 (R2)", Color = IndicatorDefaultConstants.ClassicPivotPointsR2Color, TargetSeries = new List<string> { CoreClassicPivotPointsIndicator.R2SeriesName } },
                    new SeriesColorConfig { Name = CoreClassicPivotPointsIndicator.R3SeriesName, DisplayName = "Resistance 3 (R3)", Color = IndicatorDefaultConstants.ClassicPivotPointsR3Color, TargetSeries = new List<string> { CoreClassicPivotPointsIndicator.R3SeriesName } },
                    new SeriesColorConfig { Name = CoreClassicPivotPointsIndicator.S1SeriesName, DisplayName = "Support 1 (S1)", Color = IndicatorDefaultConstants.ClassicPivotPointsS1Color, TargetSeries = new List<string> { CoreClassicPivotPointsIndicator.S1SeriesName } },
                    new SeriesColorConfig { Name = CoreClassicPivotPointsIndicator.S2SeriesName, DisplayName = "Support 2 (S2)", Color = IndicatorDefaultConstants.ClassicPivotPointsS2Color, TargetSeries = new List<string> { CoreClassicPivotPointsIndicator.S2SeriesName } },
                    new SeriesColorConfig { Name = CoreClassicPivotPointsIndicator.S3SeriesName, DisplayName = "Support 3 (S3)", Color = IndicatorDefaultConstants.ClassicPivotPointsS3Color, TargetSeries = new List<string> { CoreClassicPivotPointsIndicator.S3SeriesName } }
                }
            },
            new CoreIndicatorSettings 
            { 
                TypeEnum = IndicatorType.PivotPointsAdvanced,
                Category = CoreIndicatorCategory.Chart,
                IsEnabled = false,
                ParameterObject = new CorePivotPointsAdvancedParameter(),
                Color = IndicatorDefaultConstants.ClassicPivotPointsPivotColor,
                Thickness = IndicatorDefaultConstants.DefaultBandThickness,
                Style = CoreLineStyle.Solid,
                IsOverlay = true,
                SeriesColors = new System.Collections.ObjectModel.ObservableCollection<SeriesColorConfig>
                {
                    new SeriesColorConfig { Name = "Pivot", DisplayName = "Pivot (P)", Color = IndicatorDefaultConstants.ClassicPivotPointsPivotColor, TargetSeries = new List<string> { CorePivotPointsAdvancedIndicator.PivotSeriesName } },
                    new SeriesColorConfig { Name = CorePivotPointsAdvancedIndicator.R1SeriesName, DisplayName = "Resistance 1 (R1)", Color = IndicatorDefaultConstants.ClassicPivotPointsR1Color, TargetSeries = new List<string> { CorePivotPointsAdvancedIndicator.R1SeriesName } },
                    new SeriesColorConfig { Name = CorePivotPointsAdvancedIndicator.R2SeriesName, DisplayName = "Resistance 2 (R2)", Color = IndicatorDefaultConstants.ClassicPivotPointsR2Color, TargetSeries = new List<string> { CorePivotPointsAdvancedIndicator.R2SeriesName } },
                    new SeriesColorConfig { Name = CorePivotPointsAdvancedIndicator.R3SeriesName, DisplayName = "Resistance 3 (R3)", Color = IndicatorDefaultConstants.ClassicPivotPointsR3Color, TargetSeries = new List<string> { CorePivotPointsAdvancedIndicator.R3SeriesName } },
                    new SeriesColorConfig { Name = CorePivotPointsAdvancedIndicator.R4SeriesName, DisplayName = "Resistance 4 (R4)", Color = IndicatorDefaultConstants.ClassicPivotPointsR3Color, TargetSeries = new List<string> { CorePivotPointsAdvancedIndicator.R4SeriesName } },
                    new SeriesColorConfig { Name = CorePivotPointsAdvancedIndicator.S1SeriesName, DisplayName = "Support 1 (S1)", Color = IndicatorDefaultConstants.ClassicPivotPointsS1Color, TargetSeries = new List<string> { CorePivotPointsAdvancedIndicator.S1SeriesName } },
                    new SeriesColorConfig { Name = CorePivotPointsAdvancedIndicator.S2SeriesName, DisplayName = "Support 2 (S2)", Color = IndicatorDefaultConstants.ClassicPivotPointsS2Color, TargetSeries = new List<string> { CorePivotPointsAdvancedIndicator.S2SeriesName } },
                    new SeriesColorConfig { Name = CorePivotPointsAdvancedIndicator.S3SeriesName, DisplayName = "Support 3 (S3)", Color = IndicatorDefaultConstants.ClassicPivotPointsS3Color, TargetSeries = new List<string> { CorePivotPointsAdvancedIndicator.S3SeriesName } },
                    new SeriesColorConfig { Name = CorePivotPointsAdvancedIndicator.S4SeriesName, DisplayName = "Support 4 (S4)", Color = IndicatorDefaultConstants.ClassicPivotPointsS3Color, TargetSeries = new List<string> { CorePivotPointsAdvancedIndicator.S4SeriesName } }
                }
            },
            new CoreIndicatorSettings 
            { 
                TypeEnum = IndicatorType.StandardDeviation, 
                Category = CoreIndicatorCategory.Volatility, 
                IsEnabled = false, 
                ParameterObject = new CoreStandardDeviationParameter(), 
                Color = IndicatorDefaultConstants.RsiColor, 
                Thickness = IndicatorDefaultConstants.DefaultSubPanelThickness, 
                Style = CoreLineStyle.Solid, 
                IsOverlay = false 
            },
            new CoreIndicatorSettings 
            { 
                TypeEnum = IndicatorType.MassIndex, 
                Category = CoreIndicatorCategory.Volatility, 
                IsEnabled = false, 
                ParameterObject = new CoreMassIndexParameter(), 
                Color = IndicatorDefaultConstants.RsiColor, 
                Thickness = IndicatorDefaultConstants.DefaultSubPanelThickness, 
                Style = CoreLineStyle.Solid, 
                IsOverlay = false 
            },
            new CoreIndicatorSettings
            {
                TypeEnum = IndicatorType.CrossTickerDtw,
                Category = CoreIndicatorCategory.Other,
                IsEnabled = false,
                ParameterObject = new CoreCrossTickerDtwParameter(),
                Color = IndicatorDefaultConstants.CrossTickerDtwColor,
                Thickness = IndicatorDefaultConstants.DefaultSubPanelThickness,
                Style = CoreLineStyle.Solid,
                IsOverlay = false,
                MinValue = null,
                MaxValue = null,
                IncludeZeroBaseline = true,
                SeriesColors = new System.Collections.ObjectModel.ObservableCollection<SeriesColorConfig>
                {
                    new SeriesColorConfig { Name = "CrossTickerDtw", DisplayName = "Cross-Ticker DTW", Color = IndicatorDefaultConstants.CrossTickerDtwColor, TargetSeries = new List<string> { "Main" } }
                }
            },
            new CoreIndicatorSettings
            {
                TypeEnum = IndicatorType.PACF,
                Category = CoreIndicatorCategory.Other,
                IsEnabled = false,
                ParameterObject = new CorePacfParameter(),
                Color = IndicatorDefaultConstants.PacfColor,
                Thickness = IndicatorDefaultConstants.DefaultSubPanelThickness,
                Style = CoreLineStyle.Solid,
                IsOverlay = false,
                MinValue = IndicatorDefaultConstants.PacfMinValue,
                MaxValue = IndicatorDefaultConstants.PacfMaxValue,
                IncludeZeroBaseline = true,
                SeriesColors = new System.Collections.ObjectModel.ObservableCollection<SeriesColorConfig>
                {
                    new SeriesColorConfig { Name = "PACF", DisplayName = "PACF", Color = IndicatorDefaultConstants.PacfColor, TargetSeries = new List<string> { "Main" } },
                    new SeriesColorConfig { Name = "ConfidenceBand", DisplayName = "95% Confidence Band", Color = IndicatorDefaultConstants.PacfConfidenceBandColor, TargetSeries = new List<string> { "UpperConfidenceBand", "LowerConfidenceBand" } }
                }
            },
            new CoreIndicatorSettings
            {
                TypeEnum = IndicatorType.RankCorrelation,
                Category = CoreIndicatorCategory.Other,
                IsEnabled = false,
                ParameterObject = new CoreRankCorrelationParameter(),
                Color = IndicatorDefaultConstants.RankCorrelationColor,
                Thickness = IndicatorDefaultConstants.DefaultSubPanelThickness,
                Style = CoreLineStyle.Solid,
                IsOverlay = false,
                MinValue = IndicatorDefaultConstants.RankCorrelationMinValue,
                MaxValue = IndicatorDefaultConstants.RankCorrelationMaxValue,
                IncludeZeroBaseline = true,
                SeriesColors = new System.Collections.ObjectModel.ObservableCollection<SeriesColorConfig>
                {
                    new SeriesColorConfig { Name = "RankCorrelation", DisplayName = "Rank Correlation", Color = IndicatorDefaultConstants.RankCorrelationColor, TargetSeries = new List<string> { "Main" } }
                }
            },
            new CoreIndicatorSettings
            {
                // Colours by role (decision U-01=A): trend main line = SmaColor, second line = StochDColor.
                TypeEnum = IndicatorType.ChandelierExit,
                Category = CoreIndicatorCategory.Trend,
                IsEnabled = false,
                ParameterObject = new CoreChandelierExitParameter(),
                Color = IndicatorDefaultConstants.SmaColor,
                Thickness = IndicatorDefaultConstants.DefaultOverlayThickness,
                Style = CoreLineStyle.Solid,
                IsOverlay = true,
                SeriesColors = new System.Collections.ObjectModel.ObservableCollection<SeriesColorConfig>
                {
                    new SeriesColorConfig { Name = IndicatorResult.MainSeriesName, DisplayName = "Long Stop", Color = IndicatorDefaultConstants.SmaColor, TargetSeries = new List<string> { IndicatorResult.MainSeriesName } },
                    new SeriesColorConfig { Name = nameof(CoreChandelierExitIndicator.ShortStop), DisplayName = "Short Stop", Color = IndicatorDefaultConstants.StochDColor, TargetSeries = new List<string> { nameof(CoreChandelierExitIndicator.ShortStop) } }
                }
            },
            new CoreIndicatorSettings
            {
                TypeEnum = IndicatorType.T3,
                Category = CoreIndicatorCategory.Trend,
                IsEnabled = false,
                ParameterObject = new CoreT3Parameter(),
                Color = IndicatorDefaultConstants.SmaColor,
                Thickness = IndicatorDefaultConstants.DefaultOverlayThickness,
                Style = CoreLineStyle.Solid,
                IsOverlay = true,
                SeriesColors = new System.Collections.ObjectModel.ObservableCollection<SeriesColorConfig>
                {
                    new SeriesColorConfig { Name = IndicatorResult.MainSeriesName, DisplayName = "T3", Color = IndicatorDefaultConstants.SmaColor, TargetSeries = new List<string> { IndicatorResult.MainSeriesName } }
                }
            },
            new CoreIndicatorSettings
            {
                TypeEnum = IndicatorType.McGinleyDynamic,
                Category = CoreIndicatorCategory.Trend,
                IsEnabled = false,
                ParameterObject = new CoreMcGinleyDynamicParameter(),
                Color = IndicatorDefaultConstants.SmaColor,
                Thickness = IndicatorDefaultConstants.DefaultOverlayThickness,
                Style = CoreLineStyle.Solid,
                IsOverlay = true,
                SeriesColors = new System.Collections.ObjectModel.ObservableCollection<SeriesColorConfig>
                {
                    new SeriesColorConfig { Name = IndicatorResult.MainSeriesName, DisplayName = "McGinley Dynamic", Color = IndicatorDefaultConstants.SmaColor, TargetSeries = new List<string> { IndicatorResult.MainSeriesName } }
                }
            },
            new CoreIndicatorSettings
            {
                // Colours by role (decision U-01=A): oscillator main = RsiColor; Signal and Histogram use the MACD item's constants.
                TypeEnum = IndicatorType.TSI,
                Category = CoreIndicatorCategory.Oscillator,
                IsEnabled = false,
                ParameterObject = new CoreTsiParameter(),
                Color = IndicatorDefaultConstants.RsiColor,
                Thickness = IndicatorDefaultConstants.DefaultSubPanelThickness,
                Style = CoreLineStyle.Solid,
                IsOverlay = false,
                MinValue = IndicatorDefaultConstants.TsiMinValue,
                MaxValue = IndicatorDefaultConstants.TsiMaxValue,
                IncludeZeroBaseline = true,
                UpColor = IndicatorDefaultConstants.MacdHistogramUpColor,
                DownColor = IndicatorDefaultConstants.MacdHistogramDownColor,
                SeriesColors = new System.Collections.ObjectModel.ObservableCollection<SeriesColorConfig>
                {
                    new SeriesColorConfig { Name = IndicatorResult.MainSeriesName, DisplayName = "TSI", Color = IndicatorDefaultConstants.RsiColor, TargetSeries = new List<string> { IndicatorResult.MainSeriesName } },
                    new SeriesColorConfig { Name = nameof(CoreTsiIndicator.Signal), DisplayName = "Signal Line", Color = IndicatorDefaultConstants.MacdSignalColor, TargetSeries = new List<string> { nameof(CoreTsiIndicator.Signal) } },
                    new SeriesColorConfig { Name = nameof(CoreTsiIndicator.Histogram), DisplayName = "Histogram", Color = IndicatorDefaultConstants.MacdHistogramColor, TargetSeries = new List<string> { nameof(CoreTsiIndicator.Histogram) } }
                }
            },
            new CoreIndicatorSettings
            {
                // The two lines use the Stochastic definition (%K = StochKColor, %D = StochDColor) and the fixed 0..100 axis is the Stochastic one.
                // The generic role colours of decision U-01=A (oscillator main = RsiColor, second line = StochDColor) would give %K and %D the same purple,
                // because RsiColor and StochDColor have the same value (owner correction 2026-11-14).
                TypeEnum = IndicatorType.StochRSI,
                Category = CoreIndicatorCategory.Oscillator,
                IsEnabled = false,
                ParameterObject = new CoreStochRsiParameter(),
                Color = IndicatorDefaultConstants.StochKColor,
                Thickness = IndicatorDefaultConstants.DefaultSubPanelThickness,
                Style = CoreLineStyle.Solid,
                IsOverlay = false,
                MinValue = IndicatorDefaultConstants.StochMinValue,
                MaxValue = IndicatorDefaultConstants.StochMaxValue,
                SeriesColors = new System.Collections.ObjectModel.ObservableCollection<SeriesColorConfig>
                {
                    new SeriesColorConfig { Name = IndicatorResult.MainSeriesName, DisplayName = "%K", Color = IndicatorDefaultConstants.StochKColor, TargetSeries = new List<string> { IndicatorResult.MainSeriesName } },
                    new SeriesColorConfig { Name = nameof(CoreStochRsiIndicator.PercentD), DisplayName = "%D", Color = IndicatorDefaultConstants.StochDColor, TargetSeries = new List<string> { nameof(CoreStochRsiIndicator.PercentD) } }
                }
            },
            new CoreIndicatorSettings
            {
                // Colours by role (decision U-01=A): volume main = Cyan; Signal and Histogram use the MACD item's constants.
                TypeEnum = IndicatorType.KVO,
                Category = CoreIndicatorCategory.Volume,
                IsEnabled = false,
                ParameterObject = new CoreKvoParameter(),
                Color = IndicatorDefaultConstants.Cyan,
                Thickness = IndicatorDefaultConstants.DefaultSubPanelThickness,
                Style = CoreLineStyle.Solid,
                IsOverlay = false,
                IncludeZeroBaseline = true,
                UpColor = IndicatorDefaultConstants.MacdHistogramUpColor,
                DownColor = IndicatorDefaultConstants.MacdHistogramDownColor,
                SeriesColors = new System.Collections.ObjectModel.ObservableCollection<SeriesColorConfig>
                {
                    new SeriesColorConfig { Name = IndicatorResult.MainSeriesName, DisplayName = "KVO", Color = IndicatorDefaultConstants.Cyan, TargetSeries = new List<string> { IndicatorResult.MainSeriesName } },
                    new SeriesColorConfig { Name = nameof(CoreKvoIndicator.Signal), DisplayName = "Signal Line", Color = IndicatorDefaultConstants.MacdSignalColor, TargetSeries = new List<string> { nameof(CoreKvoIndicator.Signal) } },
                    new SeriesColorConfig { Name = nameof(CoreKvoIndicator.Histogram), DisplayName = "Histogram", Color = IndicatorDefaultConstants.MacdHistogramColor, TargetSeries = new List<string> { nameof(CoreKvoIndicator.Histogram) } }
                }
            },
            new CoreIndicatorSettings
            {
                // Colours by role (decision U-01=A): chart/trend main line = SmaColor; the upper and lower lines share the Bollinger bands constant (one "Bands" entry, as in the BB item).
                TypeEnum = IndicatorType.VolumeProfileLevels,
                Category = CoreIndicatorCategory.Chart,
                IsEnabled = false,
                ParameterObject = new CoreVolumeProfileLevelsParameter(),
                Color = IndicatorDefaultConstants.SmaColor,
                Thickness = IndicatorDefaultConstants.DefaultOverlayThickness,
                Style = CoreLineStyle.Solid,
                IsOverlay = true,
                SeriesColors = new System.Collections.ObjectModel.ObservableCollection<SeriesColorConfig>
                {
                    new SeriesColorConfig { Name = IndicatorResult.MainSeriesName, DisplayName = "POC", Color = IndicatorDefaultConstants.SmaColor, TargetSeries = new List<string> { IndicatorResult.MainSeriesName } },
                    new SeriesColorConfig { Name = "Bands", DisplayName = "Value Area (VAH/VAL)", Color = IndicatorDefaultConstants.BollingerBandsColor, TargetSeries = new List<string> { nameof(CoreVolumeProfileLevelsIndicator.VAH), nameof(CoreVolumeProfileLevelsIndicator.VAL) } }
                }
            },
            new CoreIndicatorSettings
            {
                // Colours by role (decision U-01=A): volatility main line = RsiColor.
                TypeEnum = IndicatorType.ParkinsonVolatility,
                Category = CoreIndicatorCategory.Volatility,
                IsEnabled = false,
                ParameterObject = new CoreParkinsonVolatilityParameter(),
                Color = IndicatorDefaultConstants.RsiColor,
                Thickness = IndicatorDefaultConstants.DefaultSubPanelThickness,
                Style = CoreLineStyle.Solid,
                IsOverlay = false,
                SeriesColors = new System.Collections.ObjectModel.ObservableCollection<SeriesColorConfig>
                {
                    new SeriesColorConfig { Name = IndicatorResult.MainSeriesName, DisplayName = "Parkinson Volatility", Color = IndicatorDefaultConstants.RsiColor, TargetSeries = new List<string> { IndicatorResult.MainSeriesName } }
                }
            },
            new CoreIndicatorSettings
            {
                // Colours by role (decision U-01=A): volatility main line = RsiColor.
                TypeEnum = IndicatorType.RogersSatchellVolatility,
                Category = CoreIndicatorCategory.Volatility,
                IsEnabled = false,
                ParameterObject = new CoreRogersSatchellVolatilityParameter(),
                Color = IndicatorDefaultConstants.RsiColor,
                Thickness = IndicatorDefaultConstants.DefaultSubPanelThickness,
                Style = CoreLineStyle.Solid,
                IsOverlay = false,
                SeriesColors = new System.Collections.ObjectModel.ObservableCollection<SeriesColorConfig>
                {
                    new SeriesColorConfig { Name = IndicatorResult.MainSeriesName, DisplayName = "Rogers-Satchell Volatility", Color = IndicatorDefaultConstants.RsiColor, TargetSeries = new List<string> { IndicatorResult.MainSeriesName } }
                }
            },
            new CoreIndicatorSettings
            {
                // Colours by role (decision U-01=A): volatility main line = RsiColor.
                TypeEnum = IndicatorType.Garch11,
                Category = CoreIndicatorCategory.Volatility,
                IsEnabled = false,
                ParameterObject = new CoreGarch11Parameter(),
                Color = IndicatorDefaultConstants.RsiColor,
                Thickness = IndicatorDefaultConstants.DefaultSubPanelThickness,
                Style = CoreLineStyle.Solid,
                IsOverlay = false,
                SeriesColors = new System.Collections.ObjectModel.ObservableCollection<SeriesColorConfig>
                {
                    new SeriesColorConfig { Name = IndicatorResult.MainSeriesName, DisplayName = "GARCH(1,1)", Color = IndicatorDefaultConstants.RsiColor, TargetSeries = new List<string> { IndicatorResult.MainSeriesName } }
                }
            },
            new CoreIndicatorSettings
            {
                // Colours by role (decision U-01=A): complexity main line = RsiColor; fixed axis 0..1 (dimensionless).
                TypeEnum = IndicatorType.HurstExponent,
                Category = CoreIndicatorCategory.Other,
                IsEnabled = false,
                ParameterObject = new CoreHurstExponentParameter(),
                Color = IndicatorDefaultConstants.RsiColor,
                Thickness = IndicatorDefaultConstants.DefaultSubPanelThickness,
                Style = CoreLineStyle.Solid,
                IsOverlay = false,
                MinValue = IndicatorDefaultConstants.HurstMinValue,
                MaxValue = IndicatorDefaultConstants.HurstMaxValue,
                SeriesColors = new System.Collections.ObjectModel.ObservableCollection<SeriesColorConfig>
                {
                    new SeriesColorConfig { Name = IndicatorResult.MainSeriesName, DisplayName = "Hurst Exponent", Color = IndicatorDefaultConstants.RsiColor, TargetSeries = new List<string> { IndicatorResult.MainSeriesName } }
                }
            },
            new CoreIndicatorSettings
            {
                // Colours by role (decision U-01=A): complexity main line = RsiColor; fixed axis 0..1 (normalised entropy).
                TypeEnum = IndicatorType.Entropy,
                Category = CoreIndicatorCategory.Other,
                IsEnabled = false,
                ParameterObject = new CoreEntropyParameter(),
                Color = IndicatorDefaultConstants.RsiColor,
                Thickness = IndicatorDefaultConstants.DefaultSubPanelThickness,
                Style = CoreLineStyle.Solid,
                IsOverlay = false,
                MinValue = IndicatorDefaultConstants.EntropyMinValue,
                MaxValue = IndicatorDefaultConstants.EntropyMaxValue,
                SeriesColors = new System.Collections.ObjectModel.ObservableCollection<SeriesColorConfig>
                {
                    new SeriesColorConfig { Name = IndicatorResult.MainSeriesName, DisplayName = "Entropy", Color = IndicatorDefaultConstants.RsiColor, TargetSeries = new List<string> { IndicatorResult.MainSeriesName } }
                }
            }
        };
    }

    /// <summary>
    /// Auto-heals an indicator that was loaded from an older layout version,
    /// ensuring it has the correct default SeriesColors if missing, backfilling a fixed-range
    /// Y-axis (MinValue/MaxValue) that didn't exist yet when the instance was first persisted,
    /// and updating legacy default display names if necessary.
    /// </summary>
    public static void AutoHeal(CoreIndicatorSettings indicator)
    {
        // Backfill a missing ParameterObject for indicators persisted before the reflection-based
        // fallback (IndicatorFactory.Create(type).GetDefaultSettings().ParameterObject, see
        // CoreIndicatorBase.GetDefaultSettings) was wired into every catalog-select path (e.g. CMO,
        // FRAMA persisted from a Dynamic Period Driver Registration made before that fix). Without this,
        // such an already-persisted instance keeps a permanently null ParameterObject even after the
        // fix, because the fallback only runs when a user newly selects the indicator from a catalog,
        // never when a previously-saved instance is loaded from disk. A no-op for indicator types that
        // truly have no configurable parameters (the reflection fallback also yields null for those),
        // and skipped for Price (no ParameterObject by design).
        if (indicator.ParameterObject == null && indicator.TypeEnum.HasValue && indicator.TypeEnum.Value != IndicatorType.Price)
        {
            var instance = IndicatorFactory.CreateStatic(indicator.TypeEnum.Value);
            var fallbackParameterObject = instance?.GetDefaultSettings().ParameterObject;
            if (fallbackParameterObject != null)
            {
                // The ParameterObject setter recomputes DisplayName as a side effect (see
                // CoreIndicatorSettings.ParameterObject/UpdateDisplayName), which would silently
                // overwrite an already-persisted or user-customized display name here. Save/restore
                // it around the assignment, matching the same idiom already used by Clone()/Snapshot().
                var savedDisplayName = indicator.DisplayName;
                indicator.ParameterObject = fallbackParameterObject;
                indicator.DisplayName = savedDisplayName;
            }
        }

        var defaultSettings = GetDefault().FirstOrDefault(s => s.TypeEnum == indicator.TypeEnum);
        if (defaultSettings == null)
            return;

        // Volume Profile is strictly a main-window overlay indicator and must never be placed in a sub-window panel.
        if (indicator.TypeEnum == IndicatorType.VolumeProfile)
        {
            indicator.IsOverlay = true;
            indicator.OverlayPanelId = null;
            if (indicator.Category == CoreIndicatorCategory.Volume)
            {
                indicator.Category = CoreIndicatorCategory.Chart;
            }
        }

        // Auto-heal legacy ClothoidOscillator fixed range (-100..100) back to dynamic auto-range (null).
        // Only heals if both match the legacy default constants exactly, preserving manual user overrides.
        if (indicator.TypeEnum == IndicatorType.ClothoidOscillator &&
            indicator.MinValue == IndicatorDefaultConstants.ClothoidOscillatorMinValue &&
            indicator.MaxValue == IndicatorDefaultConstants.ClothoidOscillatorMaxValue)
        {
            indicator.MinValue = null;
            indicator.MaxValue = null;
        }

        // Auto-heal legacy RollingBeta fixed range (-3..3) back to dynamic auto-range (null).
        if (indicator.TypeEnum == IndicatorType.RollingBeta &&
            indicator.MinValue == -3.0m &&
            indicator.MaxValue == 3.0m)
        {
            indicator.MinValue = null;
            indicator.MaxValue = null;
        }

        // Backfill a fixed Y-axis range (e.g. IFFT Instantaneous Phase's SineWave/LeadSine
        // -1..1 range) that was added to this type's defaults after some workspaces/templates
        // already had the indicator persisted without it -- otherwise the panel keeps
        // auto-scaling from visible data forever, even after the defaults are fixed and the
        // app rebuilt. Only fills in a missing value; never overwrites one already set, so a
        // user's manual MinValue/MaxValue customization is preserved.
        if (indicator.MinValue == null && defaultSettings.MinValue.HasValue)
        {
            indicator.MinValue = defaultSettings.MinValue;
        }
        if (indicator.MaxValue == null && defaultSettings.MaxValue.HasValue)
        {
            indicator.MaxValue = defaultSettings.MaxValue;
        }

        if (!indicator.IncludeZeroBaseline && defaultSettings.IncludeZeroBaseline)
        {
            indicator.IncludeZeroBaseline = defaultSettings.IncludeZeroBaseline;
        }

        if (defaultSettings.SeriesColors == null || defaultSettings.SeriesColors.Count == 0)
            return;

        if (indicator.SeriesColors == null || indicator.SeriesColors.Count == 0)
        {
            indicator.SeriesColors = new System.Collections.ObjectModel.ObservableCollection<SeriesColorConfig>();
            foreach (var sc in defaultSettings.SeriesColors)
            {
                indicator.SeriesColors.Add(sc.Duplicate());
            }
        }
        else
        {
            // Auto-heal updated DisplayNames for standard series configs (e.g., "FFT Cycle Period" -> "FFT Cycle")
            foreach (var defaultSc in defaultSettings.SeriesColors)
            {
                var existingSc = indicator.SeriesColors.FirstOrDefault(sc => sc.Name == defaultSc.Name || sc.TargetSeries.SequenceEqual(defaultSc.TargetSeries));
                if (existingSc != null)
                {
                    if (existingSc.DisplayName == "FFT Cycle Period")
                    {
                        existingSc.DisplayName = defaultSc.DisplayName;
                    }
                }
            }
            // Remove any legacy OptimalLag series config from CrossCorrelation (hidden from chart settings, displayed in DataWindow directly)
            if (indicator.TypeEnum == IndicatorType.CrossCorrelation)
            {
                var orphanLag = indicator.SeriesColors.FirstOrDefault(sc => sc.Name == "OptimalLag");
                if (orphanLag != null)
                {
                    indicator.SeriesColors.Remove(orphanLag);
                }
            }
            // Remove any legacy RSquared series config from RollingAlpha (now an independent indicator RollingRSquared)
            if (indicator.TypeEnum == IndicatorType.RollingAlpha)
            {
                var orphanRSquared = indicator.SeriesColors.FirstOrDefault(sc => sc.Name == "RSquared");
                if (orphanRSquared != null)
                {
                    indicator.SeriesColors.Remove(orphanRSquared);
                }
            }
            // Remove any legacy Intercept series config from CointegrationSpread (unit/scale differs from spread, hidden from chart rendering)
            if (indicator.TypeEnum == IndicatorType.CointegrationSpread)
            {
                var orphanIntercept = indicator.SeriesColors.FirstOrDefault(sc => sc.Name == CoreCointegrationSpreadIndicator.InterceptSeriesName);
                if (orphanIntercept != null)
                {
                    indicator.SeriesColors.Remove(orphanIntercept);
                }
            }
        }
    }
}
