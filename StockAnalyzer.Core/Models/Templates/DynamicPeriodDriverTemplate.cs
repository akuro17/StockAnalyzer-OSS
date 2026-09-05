using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Models.Templates;

/// <summary>
/// Represents a reusable configuration template containing a collection of dynamic period driver settings.
/// Stored independently under Data/Templates/DynamicPeriodDriver/.
/// </summary>
public class DynamicPeriodDriverTemplate : TemplateBase
{
    public override TemplateType TemplateType => TemplateType.DynamicPeriodDriver;

    private List<CoreIndicatorSettings> _indicators = new();

    /// <summary>
    /// The collection of driver indicator configurations in this template.
    /// </summary>
    public IReadOnlyList<CoreIndicatorSettings> Indicators
    {
        get => _indicators;
        init => _indicators = value != null ? new List<CoreIndicatorSettings>(value) : new List<CoreIndicatorSettings>();
    }

    /// <summary>
    /// Replaces the indicators in this template with a new collection.
    /// </summary>
    public void SetIndicators(IEnumerable<CoreIndicatorSettings> indicators)
    {
        _indicators = indicators != null ? new List<CoreIndicatorSettings>(indicators) : new List<CoreIndicatorSettings>();
    }
}
