using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Services.Analysis;

/// <summary>What the pipeline does with one indicator's settings-level time frame.</summary>
public enum IndicatorTimeframeMode
{
    /// <summary>Calculate on the chart's own bars (the path before the setting existed).</summary>
    None = 0,

    /// <summary>Calculate on higher-frame bars and carry the result onto the chart bars causally.</summary>
    Aligned = 1,

    /// <summary>The indicator's frame is finer than the chart's: refused with <see cref="IndicatorTimeframeMessages.FinerThanChart"/>.</summary>
    FinerThanChart = 2,

    /// <summary>The indicator is part of an indicator chain, which is calculated on one bar series: the setting is ignored.</summary>
    InChainForcedSame = 3,

    /// <summary>A Pivot whose own source period is finer than the chart's frame: refused like <see cref="FinerThanChart"/>; otherwise its own logic runs.</summary>
    SelfHandledFiner = 4,

    /// <summary>The setting does not apply to the indicator (policy says so): the chart's own bars are used.</summary>
    NotApplicable = 5,

    /// <summary>Monthly on a Weekly chart: refused with <see cref="IndicatorTimeframeMessages.MonthlyRequiresDailyChart"/> (a calendar month cannot be rebuilt exactly from weekly bars).</summary>
    MonthlyRequiresDailyChart = 6,

    /// <summary>The chart's frame is not one the data layer serves (not Daily, Weekly or Monthly): refused with <see cref="IndicatorTimeframeMessages.UnsupportedChartFrame"/>.</summary>
    UnsupportedChartFrame = 7,
}

/// <summary>The decision for one indicator: what to do and on which frame (see the dispatch rule of the Indicator Timeframe contract).</summary>
public readonly record struct IndicatorTimeframePlan(IndicatorTimeframeMode Mode, TimeFrame EffectiveFrame)
{
    /// <summary>True when the pipeline must not run the indicator on the chart's own bars (it is refused).</summary>
    public bool IsRefused => Mode is IndicatorTimeframeMode.FinerThanChart or IndicatorTimeframeMode.SelfHandledFiner
        or IndicatorTimeframeMode.MonthlyRequiresDailyChart or IndicatorTimeframeMode.UnsupportedChartFrame;

    /// <summary>
    /// The fixed failure text of a refused plan (the single place the pipeline takes it from). Meaningful only when <see cref="IsRefused"/>; <paramref name="chartFrame"/> is the chart's frame.
    /// </summary>
    public string RefusalMessage(TimeFrame chartFrame) => Mode switch
    {
        IndicatorTimeframeMode.MonthlyRequiresDailyChart => IndicatorTimeframeMessages.MonthlyRequiresDailyChart,
        IndicatorTimeframeMode.UnsupportedChartFrame => IndicatorTimeframeMessages.UnsupportedChartFrame,
        _ => IndicatorTimeframeMessages.FinerThanChart(EffectiveFrame, chartFrame),
    };

    /// <summary>
    /// The ids of indicators that take part in a chain: those that name a Source Indicator / Dynamic Period Driver themselves and those another indicator names.
    /// Pass the complete list (including indicators resolved from outside the chart's own list).
    /// </summary>
    public static HashSet<string> CollectChainParticipants(IReadOnlyList<CoreIndicatorSettings> allSettings)
    {
        var participants = new HashSet<string>(System.StringComparer.Ordinal);
        for (int i = 0; i < allSettings.Count; i++)
        {
            var setting = allSettings[i];
            if (!IndicatorTimeframePolicy.HasOwnChainInput(setting))
            {
                continue;
            }

            participants.Add(setting.Id);
            if (!string.IsNullOrEmpty(setting.SourceIndicatorId))
            {
                participants.Add(setting.SourceIndicatorId);
            }

            if (!string.IsNullOrEmpty(setting.DynamicPeriodIndicatorId))
            {
                participants.Add(setting.DynamicPeriodIndicatorId);
            }
        }

        return participants;
    }

    /// <summary>
    /// False for the common case (Same as Chart, no self-owned frame): the pipeline then skips classification altogether and its path is unchanged.
    /// </summary>
    public static bool NeedsPlan(CoreIndicatorSettings setting)
        => setting.Timeframe != IndicatorTimeframe.SameAsChart || setting.ParameterObject is ISelfTimeframeParameter;

    /// <summary>Classifies <paramref name="setting"/> against the chart's frame. <paramref name="chainParticipants"/> comes from <see cref="CollectChainParticipants"/>.</summary>
    public static IndicatorTimeframePlan Classify(CoreIndicatorSettings setting, TimeFrame chartFrame, ISet<string> chainParticipants)
    {
        if (setting.TypeEnum is not { } type)
        {
            return new IndicatorTimeframePlan(IndicatorTimeframeMode.None, chartFrame);
        }

        switch (IndicatorTimeframePolicy.Resolve(type, setting.ParameterObject))
        {
            case IndicatorTimeframeSupport.SelfHandled:
            {
                // Only the refusal belongs to the pipeline; at or above the chart's frame the indicator's own logic handles its source period.
                if (setting.ParameterObject is ISelfTimeframeParameter self)
                {
                    var selfFrame = self.SelfTimeframe.ToTimeFrame(chartFrame);
                    if (selfFrame < chartFrame)
                    {
                        return new IndicatorTimeframePlan(IndicatorTimeframeMode.SelfHandledFiner, selfFrame);
                    }
                }

                return new IndicatorTimeframePlan(IndicatorTimeframeMode.None, chartFrame);
            }

            case IndicatorTimeframeSupport.NotApplicable:
                return new IndicatorTimeframePlan(IndicatorTimeframeMode.NotApplicable, chartFrame);
        }

        if (setting.Timeframe == IndicatorTimeframe.SameAsChart)
        {
            return new IndicatorTimeframePlan(IndicatorTimeframeMode.None, chartFrame);
        }

        if (chainParticipants.Contains(setting.Id))
        {
            return new IndicatorTimeframePlan(IndicatorTimeframeMode.InChainForcedSame, chartFrame);
        }

        // Higher-frame bars are built from the chart's bars only for the frames the data layer serves; an intraday frame is refused, never calculated in silence.
        if (!DataServiceTimeFrames.Supported.Contains(chartFrame))
        {
            return new IndicatorTimeframePlan(IndicatorTimeframeMode.UnsupportedChartFrame, chartFrame);
        }

        var effective = setting.Timeframe.ToTimeFrame(chartFrame);
        if (effective < chartFrame)
        {
            return new IndicatorTimeframePlan(IndicatorTimeframeMode.FinerThanChart, effective);
        }

        if (effective == chartFrame)
        {
            return new IndicatorTimeframePlan(IndicatorTimeframeMode.None, chartFrame);
        }

        // Monthly is calculated from daily bars only (a calendar month is not a whole number of weeks).
        return chartFrame == TimeFrame.W1 && effective == TimeFrame.MN1
            ? new IndicatorTimeframePlan(IndicatorTimeframeMode.MonthlyRequiresDailyChart, effective)
            : new IndicatorTimeframePlan(IndicatorTimeframeMode.Aligned, effective);
    }
}
