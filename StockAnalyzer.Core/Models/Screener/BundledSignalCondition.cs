using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace StockAnalyzer.Core.Models.Screener;

public enum SignalTargetType
{
    None,
    Long,
    ExitLong,
    StopLossLong,
    Short,
    ExitShort,
    StopLossShort
}

public partial class BundledSignalCondition : ObservableObject
{
    [JsonInclude]
    public Guid Id { get; set; } = Guid.NewGuid();

    [ObservableProperty]
    private string _name = "Custom Condition";

    [ObservableProperty]
    private SignalTargetType _targetType = SignalTargetType.Long;

    [ObservableProperty]
    private bool _isHit;

    [ObservableProperty]
    private string _statusText = "-";

    [JsonInclude]
    public ObservableCollection<ScreenerIndicatorEntry> Conditions { get; set; } = new();

    public BundledSignalCondition()
    {
    }

    public BundledSignalCondition(string name, SignalTargetType targetType, IEnumerable<ScreenerIndicatorEntry>? conditions = null)
    {
        _name = name;
        _targetType = targetType;
        if (conditions != null)
        {
            foreach (var c in conditions)
            {
                Conditions.Add(c);
            }
        }
    }
}
