using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models.Templates;

namespace StockAnalyzer.Core.Interfaces;

/// <summary>
/// Service interface responsible for managing, persisting, validating, and migrating domain templates.
/// </summary>
public interface ITemplateService
{
    /// <summary>
    /// Retrieves a specific template by its unique identifier.
    /// </summary>
    Task<T?> GetAsync<T>(TemplateType type, Guid id) where T : TemplateBase;

    /// <summary>
    /// Retrieves all templates of the specified type.
    /// </summary>
    Task<IReadOnlyList<T>> GetAllAsync<T>(TemplateType type) where T : TemplateBase;

    /// <summary>
    /// Saves or updates a template using atomic disk write operations.
    /// </summary>
    Task SaveAsync<T>(T template) where T : TemplateBase;

    /// <summary>
    /// Deletes a template by its unique identifier.
    /// </summary>
    Task<bool> DeleteAsync(TemplateType type, Guid id);

    /// <summary>
    /// Validates a template prior to application or persistence.
    /// </summary>
    Task<TemplateValidationResult> ValidateAsync<T>(T template) where T : TemplateBase;

    /// <summary>
    /// Ensures any legacy template configurations are migrated to the modern directory/GUID structure.
    /// </summary>
    Task EnsureMigratedAsync();
}
