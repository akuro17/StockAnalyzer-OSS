using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Models.Templates;

/// <summary>
/// Represents a reusable configuration template containing a collection of indicator settings.
/// </summary>
public class IndicatorTemplate : TemplateBase
{
    public override TemplateType TemplateType => TemplateType.Indicator;

    private List<CoreIndicatorSettings> _indicators = new();

    /// <summary>
    /// The collection of indicator configurations in this template.
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
