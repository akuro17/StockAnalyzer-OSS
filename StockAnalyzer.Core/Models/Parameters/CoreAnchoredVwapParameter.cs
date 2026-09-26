using System;
using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreAnchoredVwapParameter : CoreIndicatorParameterBase
{
    private int _anchorIndex = 0;

    [DisplayName("Anchor Index")]
    [Description("Anchor bar index for Anchored VWAP.")]
    [CoreParameterRange(0, 1000000)]
    public int AnchorIndex
    {
        get => _anchorIndex;
        set => SetProperty(ref _anchorIndex, value);
    }

    public override string GetDisplayName(string type) => $"{type} (Idx: {AnchorIndex})";

    public override void Validate()
    {
        if (AnchorIndex < 0) throw new ArgumentOutOfRangeException(nameof(AnchorIndex));
    }
}
