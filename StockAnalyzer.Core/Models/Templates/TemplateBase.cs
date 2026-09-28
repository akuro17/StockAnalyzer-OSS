using System;
using System.Text.Json.Serialization;

namespace StockAnalyzer.Core.Models.Templates;

/// <summary>
/// Base class for all persistable templates across the application.
/// Ensures standard identity (GUID), schema versioning, naming, and metadata.
/// </summary>
public abstract class TemplateBase
{
    public const int DefaultSchemaVersion = 1;

    /// <summary>
    /// Unique identifier for this template instance.
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Human-readable display name of the template.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Type discriminator indicating the template's functional domain.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public abstract TemplateType TemplateType { get; }

    /// <summary>
    /// Schema version for format migration and backward compatibility.
    /// </summary>
    public int SchemaVersion { get; set; } = DefaultSchemaVersion;

    /// <summary>
    /// Optional description providing context or usage hints for the template.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// UTC timestamp when the template was originally created.
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// UTC timestamp when the template was last modified.
    /// </summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Indicates whether this template is a built-in system default.
    /// </summary>
    public bool IsSystem { get; set; }

    /// <summary>
    /// Indicates whether the user has marked this template as a favorite.
    /// </summary>
    public bool IsFavorite { get; set; }
}
