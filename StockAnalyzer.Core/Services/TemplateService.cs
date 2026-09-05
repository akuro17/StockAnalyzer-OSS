using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Templates;
using StockAnalyzer.Core.Serialization;
using StockAnalyzer.Core.Utilities;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// Service responsible for managing, persisting, validating, and migrating domain templates.
/// Stores individual templates as GUID-based JSON files under Data/Templates/{TemplateType}/.
/// </summary>
public class TemplateService : ITemplateService, IDisposable
{
    private readonly ILogger<TemplateService> _logger;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private bool _isMigrated;
    private bool _isDisposed;

    public TemplateService() : this(null)
    {
    }

    public TemplateService(ILogger<TemplateService>? logger)
    {
        _logger = logger ?? NullLogger<TemplateService>.Instance;
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            TypeInfoResolver = WorkspacePolymorphicResolver.CreateResolver(),
            IncludeFields = true,
            PropertyNameCaseInsensitive = true,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
            Converters = { new IndicatorColorJsonConverter() }
        };
    }

    /// <inheritdoc />
    public async Task<T?> GetAsync<T>(TemplateType type, Guid id) where T : TemplateBase
    {
        await EnsureMigratedAsync().ConfigureAwait(false);

        var filePath = PathDiscovery.ResolveTemplatePath(type, $"{id}.json");
        if (!File.Exists(filePath))
        {
            // Also check standard "N" format without hyphens for compatibility
            var fallbackPath = PathDiscovery.ResolveTemplatePath(type, $"{id:N}.json");
            if (File.Exists(fallbackPath))
            {
                filePath = fallbackPath;
            }
            else
            {
                return null;
            }
        }

        await _semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            await using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
            var template = await JsonSerializer.DeserializeAsync<T>(fileStream, _jsonOptions).ConfigureAwait(false);
            return template;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load template of type {Type} with ID {Id} from {FilePath}", type, id, filePath);
            return null;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<T>> GetAllAsync<T>(TemplateType type) where T : TemplateBase
    {
        await EnsureMigratedAsync().ConfigureAwait(false);

        var dir = PathDiscovery.ResolveTemplatesDirectory(type);
        if (!Directory.Exists(dir))
        {
            return Array.Empty<T>();
        }

        string[] files;
        try
        {
            files = Directory.GetFiles(dir, "*.json");
        }
        catch (DirectoryNotFoundException)
        {
            return Array.Empty<T>();
        }

        var result = new List<T>();

        await _semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var file in files)
            {
                try
                {
                    await using var fileStream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
                    var template = await JsonSerializer.DeserializeAsync<T>(fileStream, _jsonOptions).ConfigureAwait(false);
                    if (template != null)
                    {
                        result.Add(template);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to read template file: {FilePath}", file);
                }
            }
        }
        finally
        {
            _semaphore.Release();
        }

        // Return ordered by IsFavorite desc, then Name asc
        return result
            .OrderByDescending(t => t.IsFavorite)
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <inheritdoc />
    public async Task SaveAsync<T>(T template) where T : TemplateBase
    {
        if (template == null) throw new ArgumentNullException(nameof(template));
        if (string.IsNullOrWhiteSpace(template.Name)) throw new ArgumentException("Template name cannot be empty.", nameof(template));
        if (template.Id == Guid.Empty) throw new ArgumentException("Template ID must be a non-empty GUID.", nameof(template));

        await EnsureMigratedAsync().ConfigureAwait(false);

        template.UpdatedAt = DateTime.UtcNow;

        var dir = PathDiscovery.ResolveTemplatesDirectory(template.TemplateType);
        var targetPath = Path.Combine(dir, $"{template.Id}.json");
        var tempPath = targetPath + ".tmp." + Guid.NewGuid().ToString("N");

        await _semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            try
            {
                await using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
                {
                    await JsonSerializer.SerializeAsync(fileStream, template, _jsonOptions).ConfigureAwait(false);
                    await fileStream.FlushAsync().ConfigureAwait(false);
                }

                // Atomic replace
                AtomicReplaceFile(tempPath, targetPath);
                _logger.LogInformation("Saved template '{Name}' ({Id}) of type {Type} to {FilePath}", template.Name, template.Id, template.TemplateType, targetPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to atomically save template {Id} to {FilePath}", template.Id, targetPath);
                throw new IOException($"Failed to save template to '{targetPath}'.", ex);
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    try { File.Delete(tempPath); } catch { /* Ignore temp cleanup errors */ }
                }
            }
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(TemplateType type, Guid id)
    {
        await EnsureMigratedAsync().ConfigureAwait(false);

        var filePath = PathDiscovery.ResolveTemplatePath(type, $"{id}.json");
        if (!File.Exists(filePath))
        {
            var fallbackPath = PathDiscovery.ResolveTemplatePath(type, $"{id:N}.json");
            if (File.Exists(fallbackPath))
            {
                filePath = fallbackPath;
            }
            else
            {
                return false;
            }
        }

        await _semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            File.Delete(filePath);
            _logger.LogInformation("Deleted template {Id} of type {Type} from {FilePath}", id, type, filePath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete template {Id} from {FilePath}", id, filePath);
            throw new IOException($"Failed to delete template '{filePath}'.", ex);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <inheritdoc />
    public Task<TemplateValidationResult> ValidateAsync<T>(T template) where T : TemplateBase
    {
        if (template == null)
        {
            return Task.FromResult(TemplateValidationResult.Failure("Template cannot be null."));
        }

        if (string.IsNullOrWhiteSpace(template.Name))
        {
            return Task.FromResult(TemplateValidationResult.Failure("Template name cannot be empty."));
        }

        if (template.Id == Guid.Empty)
        {
            return Task.FromResult(TemplateValidationResult.Failure("Template ID must be a non-empty GUID."));
        }

        if (template.SchemaVersion <= 0)
        {
            return Task.FromResult(TemplateValidationResult.Failure("Schema version must be positive."));
        }

        if (template is IndicatorTemplate indTemplate)
        {
            if (indTemplate.Indicators == null || indTemplate.Indicators.Count == 0)
            {
                return Task.FromResult(TemplateValidationResult.WithWarning("Indicator template contains no indicators."));
            }
        }
        else if (template is ColumnTemplate colTemplate)
        {
            if (colTemplate.ColumnNames == null || colTemplate.ColumnNames.Count == 0)
            {
                return Task.FromResult(TemplateValidationResult.WithWarning("Column template contains no column names."));
            }
        }
        else if (template is ThemeTemplate themeTemplate)
        {
            if (themeTemplate.Colors == null)
            {
                return Task.FromResult(TemplateValidationResult.Failure("Theme template contains no colors."));
            }
        }
        else if (template is FilterTemplate filterTemplate)
        {
            if (filterTemplate.RootSettings == null)
            {
                return Task.FromResult(TemplateValidationResult.Failure("Filter template contains no root settings."));
            }

            if (filterTemplate.RootSettings.Rules.Count == 0 && filterTemplate.RootSettings.Children.Count == 0)
            {
                return Task.FromResult(TemplateValidationResult.WithWarning("Filter template contains no rules or child filters."));
            }
        }
        else if (template is FeatureSpecTemplate featureTemplate)
        {
            if (featureTemplate.Spec == null || featureTemplate.Spec.Channels.Count == 0)
            {
                return Task.FromResult(TemplateValidationResult.WithWarning("Feature template contains no channels."));
            }
        }
        else if (template is SourceIndicatorTemplate srcTemplate)
        {
            if (srcTemplate.Indicators == null || srcTemplate.Indicators.Count == 0)
            {
                return Task.FromResult(TemplateValidationResult.WithWarning("Source indicator template contains no indicators."));
            }
        }
        else if (template is DynamicPeriodDriverTemplate driverTemplate)
        {
            if (driverTemplate.Indicators == null || driverTemplate.Indicators.Count == 0)
            {
                return Task.FromResult(TemplateValidationResult.WithWarning("Dynamic period driver template contains no indicators."));
            }
        }

        return Task.FromResult(TemplateValidationResult.Success());
    }

    /// <inheritdoc />
    public async Task EnsureMigratedAsync()
    {
        if (_isMigrated) return;

        await _semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_isMigrated) return;

            await MigrateLegacyIndicatorTemplatesAsync().ConfigureAwait(false);
            await MigrateLegacyColumnTemplatesAsync().ConfigureAwait(false);

            _isMigrated = true;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task MigrateLegacyIndicatorTemplatesAsync()
    {
        var legacyConfigPath = PathDiscovery.ResolveConfigPath("indicator_templates.json");
        if (!File.Exists(legacyConfigPath)) return;

        try
        {
            var json = await File.ReadAllTextAsync(legacyConfigPath).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json) || json.Trim() == "[]")
            {
                RenameLegacyFile(legacyConfigPath);
                return;
            }

            var legacyTemplates = JsonSerializer.Deserialize<List<LegacyIndicatorTemplateDto>>(json, _jsonOptions);
            if (legacyTemplates != null && legacyTemplates.Count > 0)
            {
                var targetDir = PathDiscovery.ResolveTemplatesDirectory(TemplateType.Indicator);
                if (!Directory.Exists(targetDir))
                {
                    Directory.CreateDirectory(targetDir);
                }

                foreach (var legacy in legacyTemplates)
                {
                    if (string.IsNullOrWhiteSpace(legacy.Name)) continue;

                    var newTemplate = new IndicatorTemplate
                    {
                        Id = Guid.NewGuid(),
                        Name = legacy.Name.Trim(),
                        SchemaVersion = 1,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow,
                        Indicators = legacy.Indicators ?? new List<CoreIndicatorSettings>()
                    };

                    var filePath = Path.Combine(targetDir, $"{newTemplate.Id}.json");
                    await using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
                    await JsonSerializer.SerializeAsync(stream, newTemplate, _jsonOptions).ConfigureAwait(false);
                    await stream.FlushAsync().ConfigureAwait(false);
                    _logger.LogInformation("Migrated legacy indicator template '{Name}' -> {Id}", newTemplate.Name, newTemplate.Id);
                }
            }

            RenameLegacyFile(legacyConfigPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to migrate legacy indicator templates from {FilePath}", legacyConfigPath);
        }
    }

    private async Task MigrateLegacyColumnTemplatesAsync()
    {
        var legacyConfigPath = PathDiscovery.ResolveConfigPath("column_templates.json");
        if (!File.Exists(legacyConfigPath)) return;

        try
        {
            var json = await File.ReadAllTextAsync(legacyConfigPath).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json) || json.Trim() == "[]")
            {
                RenameLegacyFile(legacyConfigPath);
                return;
            }

            var legacyTemplates = JsonSerializer.Deserialize<List<LegacyColumnTemplateDto>>(json, _jsonOptions);
            if (legacyTemplates != null && legacyTemplates.Count > 0)
            {
                var targetDir = PathDiscovery.ResolveTemplatesDirectory(TemplateType.Column);
                if (!Directory.Exists(targetDir))
                {
                    Directory.CreateDirectory(targetDir);
                }

                foreach (var legacy in legacyTemplates)
                {
                    if (string.IsNullOrWhiteSpace(legacy.Name)) continue;

                    var newTemplate = new ColumnTemplate
                    {
                        Id = Guid.NewGuid(),
                        Name = legacy.Name.Trim(),
                        SchemaVersion = 1,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow,
                        ColumnNames = legacy.ColumnNames ?? new List<string>()
                    };

                    var filePath = Path.Combine(targetDir, $"{newTemplate.Id}.json");
                    await using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
                    await JsonSerializer.SerializeAsync(stream, newTemplate, _jsonOptions).ConfigureAwait(false);
                    await stream.FlushAsync().ConfigureAwait(false);
                    _logger.LogInformation("Migrated legacy column template '{Name}' -> {Id}", newTemplate.Name, newTemplate.Id);
                }
            }

            RenameLegacyFile(legacyConfigPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to migrate legacy column templates from {FilePath}", legacyConfigPath);
        }
    }

    private void RenameLegacyFile(string legacyFilePath)
    {
        try
        {
            var migratedPath = legacyFilePath + ".migrated";
            if (File.Exists(migratedPath))
            {
                File.Delete(migratedPath);
            }
            File.Move(legacyFilePath, migratedPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not rename migrated legacy file {FilePath}", legacyFilePath);
        }
    }

    private static void AtomicReplaceFile(string tempPath, string targetPath)
    {
        if (File.Exists(targetPath))
        {
            try
            {
                File.Replace(tempPath, targetPath, null);
            }
            catch
            {
                File.Move(tempPath, targetPath, overwrite: true);
            }
        }
        else
        {
            File.Move(tempPath, targetPath, overwrite: true);
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _semaphore.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed class LegacyIndicatorTemplateDto
    {
        public string Name { get; set; } = string.Empty;
        public List<CoreIndicatorSettings>? Indicators { get; set; }
    }

    private sealed class LegacyColumnTemplateDto
    {
        public string Name { get; set; } = string.Empty;
        public List<string>? ColumnNames { get; set; }
    }
}
