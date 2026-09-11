using System;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreRsiParameter : CoreSmaParameter
{
     public override string GetDisplayName(string type) => $"{type} ({Period})";
}
