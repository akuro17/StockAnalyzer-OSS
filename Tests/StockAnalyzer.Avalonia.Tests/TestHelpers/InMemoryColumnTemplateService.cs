using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models.Templates;

namespace StockAnalyzer.Avalonia.Tests.TestHelpers;

/// <summary>
/// In-memory <see cref="ITemplateService"/> for <see cref="ColumnTemplate"/> tests. Like the real
/// TemplateService it hands out fresh instances on every read, so tests notice stale-instance bugs.
/// </summary>
public sealed class InMemoryColumnTemplateService : ITemplateService
{
    /// <summary>The persisted templates (the "disk").</summary>
    public List<ColumnTemplate> Templates { get; } = new();

    public int SaveCount { get; private set; }

    /// <summary>Every template instance passed to <see cref="SaveAsync{T}"/>, in call order.</summary>
    public List<ColumnTemplate> Saved { get; } = new();

    /// <summary>When set, <see cref="SaveAsync{T}"/> does not persist until this task completes (a slow disk write).</summary>
    public Task? SaveGate { get; set; }

    /// <summary>Templates with this name fail validation.</summary>
    public string? InvalidTemplateName { get; set; }

    private static ColumnTemplate Copy(ColumnTemplate t) =>
        new() { Id = t.Id, Name = t.Name, ColumnNames = t.ColumnNames.ToList() };

    public Task<T?> GetAsync<T>(TemplateType type, Guid id) where T : TemplateBase
    {
        var found = Templates.FirstOrDefault(t => t.Id == id);
        return Task.FromResult(found == null ? null : (T?)(object)Copy(found));
    }

    public Task<IReadOnlyList<T>> GetAllAsync<T>(TemplateType type) where T : TemplateBase
    {
        if (typeof(T) != typeof(ColumnTemplate))
        {
            return Task.FromResult<IReadOnlyList<T>>(Array.Empty<T>());
        }
        return Task.FromResult<IReadOnlyList<T>>(Templates.Select(t => (T)(object)Copy(t)).ToList());
    }

    public async Task SaveAsync<T>(T template) where T : TemplateBase
    {
        SaveCount++;
        if (SaveGate != null) await SaveGate;
        var col = (ColumnTemplate)(object)template;
        Saved.Add(col);
        Templates.RemoveAll(t => t.Id == col.Id);
        Templates.Add(Copy(col));
    }

    public Task<bool> DeleteAsync(TemplateType type, Guid id) => Task.FromResult(Templates.RemoveAll(t => t.Id == id) > 0);

    public Task<TemplateValidationResult> ValidateAsync<T>(T template) where T : TemplateBase =>
        Task.FromResult(InvalidTemplateName != null && template.Name == InvalidTemplateName
            ? TemplateValidationResult.Failure("invalid")
            : TemplateValidationResult.Success());

    public Task EnsureMigratedAsync() => Task.CompletedTask;
}
