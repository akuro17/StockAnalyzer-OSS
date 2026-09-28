using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Models.Templates;

public enum TemplateValidationSeverity
{
    Valid = 0,
    Warning = 1,
    Invalid = 2
}

/// <summary>
/// Represents the result of validating a template prior to application or persistence.
/// </summary>
public sealed record TemplateValidationResult
{
    public TemplateValidationSeverity Severity { get; init; } = TemplateValidationSeverity.Valid;
    public IReadOnlyList<string> Errors { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];

    public bool IsValid => Severity != TemplateValidationSeverity.Invalid && Errors.Count == 0;

    public static TemplateValidationResult Success() => new()
    {
        Severity = TemplateValidationSeverity.Valid
    };

    public static TemplateValidationResult WithWarning(params string[] warnings) => new()
    {
        Severity = TemplateValidationSeverity.Warning,
        Warnings = warnings.ToList()
    };

    public static TemplateValidationResult Failure(params string[] errors) => new()
    {
        Severity = TemplateValidationSeverity.Invalid,
        Errors = errors.ToList()
    };
}
