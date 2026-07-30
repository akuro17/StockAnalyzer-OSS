using System;
using System.Collections.Generic;

namespace StockAnalyzer.Core.Models.Settings
{
    public class FilterSettings
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = "New Filter";
        public Guid ParentId { get; set; }
        public List<FilterRule> Rules { get; set; } = new();
        public List<FilterSettings> Children { get; set; } = new();
    }

    public class FilterRule
    {
        public string Field { get; set; } = "Tag"; // Default field is "Tag"
        public string Operator { get; set; } = "Contains"; // "Equals", "NotEquals", "Contains", "NotContains"
        public string Value { get; set; } = string.Empty;
        public bool IsCompareToField { get; set; } = false;
    }
}
