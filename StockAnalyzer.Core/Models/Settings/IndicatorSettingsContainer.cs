using System.Collections.Generic;

namespace StockAnalyzer.Core.Models.Settings
{
    public class IndicatorSettingsContainer
    {
        public int Version { get; set; } = 1;
        public List<IndicatorSetting> Settings { get; set; } = new();
    }
}
