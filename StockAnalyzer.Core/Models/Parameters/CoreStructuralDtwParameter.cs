using System;
using System.ComponentModel;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Constants;

namespace StockAnalyzer.Core.Models.Parameters
{
    public class CoreStructuralDtwParameter : CoreIndicatorParameterBase
    {
        [DisplayName("Period")]
        [Description("The window size (number of candles) used for waveform comparison.")]
        [Category("Waveform")]
        [CoreParameterRange(1, 200)]
        [ParameterTag(ParameterTags.DynamicPeriodSensitive)]
        public int Period { get; set; } = 14;

        [DisplayName("Lag")]
        [Description("How many candles back to look for the comparison waveform.")]
        [Category("Waveform")]
        [CoreParameterRange(0, 200)]
        public int Lag { get; set; } = 14;

        [DisplayName("Warping Radius")]
        [Description("Sakoe-Chiba constraint limiting time-warping in DTW. 0 disables the constraint.")]
        [Category("DTW Constraint")]
        [CoreParameterRange(0, 50)]
        public int WarpingRadius { get; set; } = ChartConstants.DtwDefaultWarpingRadius;

        public override string GetDisplayName(string indicatorType)
        {
            return $"DTW_Osc({Period},{Lag})";
        }

        public override void Validate()
        {
            // Optional basic validation mechanism
            if (Period < 1) throw new ArgumentOutOfRangeException(nameof(Period), "Period must be >= 1");
            if (Lag < 0) throw new ArgumentOutOfRangeException(nameof(Lag), "Lag must be >= 0");
        }

        public override bool Equals(object? obj)
        {
            if (obj is not CoreStructuralDtwParameter p) return false;
            return p.Period == this.Period && p.Lag == this.Lag && p.WarpingRadius == this.WarpingRadius;
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(Period, Lag, WarpingRadius);
        }
    }
}
