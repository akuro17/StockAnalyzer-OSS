using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace StockAnalyzer.Core.Models.Parameters;

public enum RocDisplayStyle
{
    [Description("Line")]
    Line,
    [Description("Histogram")]
    Histogram
}

public class CoreRocParameter : CoreIndicatorParameterBase
{
    private int _period = 12;
    [CoreParameterRange(1, 1000)]
    [Range(1, 1000)]
    [DisplayName("Period")]
    [Description("Number of periods for Rate of Change.")]
    public int Period 
    { 
        get => _period; 
        set => SetProperty(ref _period, value); 
    }

    private RocDisplayStyle _displayStyle = RocDisplayStyle.Line;
    [DisplayName("Display Style")]
    [Description("Choose whether to draw as a Line or a Histogram.")]
    public RocDisplayStyle DisplayStyle 
    { 
        get => _displayStyle; 
        set => SetProperty(ref _displayStyle, value); 
    }

    public override string GetDisplayName(string type) => $"{type} ({Period}, {DisplayStyle})";

    public override void Validate()
    {
        if (Period <= 0 || Period > 1000)
            throw new ArgumentOutOfRangeException(nameof(Period), "Period must be between 1 and 1000");
    }
}
