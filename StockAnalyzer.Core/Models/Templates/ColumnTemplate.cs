using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Models.Templates;

/// <summary>
/// Represents a reusable configuration template containing a customized column layout for grids/watchlists.
/// </summary>
public class ColumnTemplate : TemplateBase
{
    public override TemplateType TemplateType => TemplateType.Column;

    private List<string> _columnNames = new();

    /// <summary>
    /// The ordered list of visible column member names.
    /// </summary>
    public IReadOnlyList<string> ColumnNames
    {
        get => _columnNames;
        init => _columnNames = value != null ? new List<string>(value) : new List<string>();
    }

    /// <summary>
    /// Replaces the column names in this template with a new collection.
    /// </summary>
    public void SetColumnNames(IEnumerable<string> columnNames)
    {
        _columnNames = columnNames != null ? new List<string>(columnNames) : new List<string>();
    }
}
