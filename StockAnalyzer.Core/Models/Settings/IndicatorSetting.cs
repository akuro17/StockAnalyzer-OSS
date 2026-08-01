using System.Collections.Generic;

namespace StockAnalyzer.Core.Models.Settings
{
    public class IndicatorSetting
    {
        public string TypeName { get; set; } = string.Empty;
        public Dictionary<string, object> Parameters { get; set; } = new();
    }
}
