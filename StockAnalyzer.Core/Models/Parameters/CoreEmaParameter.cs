using System;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreEmaParameter : CoreSmaParameter
{
     public override string GetDisplayName(string type) => $"{type} ({Period})";
}
