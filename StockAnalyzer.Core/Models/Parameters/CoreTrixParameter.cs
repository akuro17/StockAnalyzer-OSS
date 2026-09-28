using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreTrixParameter : CoreIndicatorParameterBase
{
    private int _period = 15;

    [CoreParameterRange(1, 10000)]
    [Range(1, 1000)]
    [DisplayName("Period")]
    [Description("Number of periods for each of TRIX's triple-smoothed EMA stages.")]
    public int Period
    {
        get => _period;
        set => SetProperty(ref _period, value);
    }

    public override string GetDisplayName(string type) => $"{type} ({Period})";

    public override int GetRequiredWarmupBars() => 3 * Period - 1;

    public override void Validate()
    {
        if (Period <= 0 || Period > 10000)
            throw new ArgumentOutOfRangeException(nameof(Period), "Period must be between 1 and 10000");
    }
}
