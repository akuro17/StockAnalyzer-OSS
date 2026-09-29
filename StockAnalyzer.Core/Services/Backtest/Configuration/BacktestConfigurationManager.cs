using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Configuration;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Serialization;
using StockAnalyzer.Core.Services.Backtest.Engine;

namespace StockAnalyzer.Core.Services.Backtest.Configuration;

/// <summary>
/// Loads/saves <see cref="BacktestConfigurationDto"/> to
/// <c>PathDiscovery.ResolveConfigPath("backtest_configuration.json")</c> (spec §5.5), verbatim per
/// the source spec's own path choice - unlike the P2 reporting-settings/export files, this one does
/// not get its own dedicated <c>PathDiscovery.Resolve*</c> helper. A <see cref="SemaphoreSlim"/>
/// serializes every read/write against this single path (spec §5.5 requirement), and
/// <see cref="AtomicJsonFile"/> supplies the temp-file-then-replace write mechanics already used by
/// every other settings manager in the app. Unlike <see cref="Reporting.BacktestReportSettingsManager"/>
/// (which reports a built-in fallback for absent or invalid content), this manager surfaces a corrupt
/// file, JSON null, or unknown SchemaVersion as an explicit <see cref="BacktestConfigurationLoadStatus.Error"/>
/// instead of silently discarding it - spec §5.5 requires the UI and the original file to be left
/// untouched in that case, which only the caller (the Configuration tab ViewModel) can guarantee.
/// </summary>
public sealed class BacktestConfigurationManager : IBacktestConfigurationManager
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly IStockAnalyzerSettings _settings;
    private readonly ILogger<BacktestConfigurationManager> _logger;

    public BacktestConfigurationManager(
        IStockAnalyzerSettings settings,
        string? filePath = null,
        ILogger<BacktestConfigurationManager>? logger = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? NullLogger<BacktestConfigurationManager>.Instance;
        _filePath = filePath ?? PathDiscovery.ResolveConfigPath("backtest_configuration.json");
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            TypeInfoResolver = WorkspacePolymorphicResolver.CreateResolver(),
            IncludeFields = true,
            PropertyNameCaseInsensitive = true,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        };
    }

    public async Task<BacktestConfigurationLoadResult> LoadAsync()
    {
        await _semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            BacktestConfigurationDto? dto;
            try
            {
                dto = await AtomicJsonFile.LoadAsync<BacktestConfigurationDto?>(_filePath, _jsonOptions).ConfigureAwait(false);
            }
            catch (FileNotFoundException)
            {
                return BacktestConfigurationLoadResult.NoFileDefaultsApplied();
            }
            catch (DirectoryNotFoundException)
            {
                return BacktestConfigurationLoadResult.NoFileDefaultsApplied();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Backtest configuration could not be loaded from {FilePath}.", _filePath);
                return BacktestConfigurationLoadResult.Error(ex.Message);
            }

            return ValidateDeserializedConfiguration(dto, _settings.BacktestMaxConditionOffset, _settings.BacktestMaxConditionTreeDepth, _settings.BacktestMaxConditionTreeNodes, _settings.BacktestMaxConditionGroupNameLength);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// Spec §5.5 post-deserialization validation (null/SchemaVersion/enum-boundary checks), extracted
    /// from <see cref="LoadAsync"/> so it can be exercised directly by xUnit without touching the real
    /// <c>FilePath</c> under the user's actual Data/Config folder (see
    /// <c>BacktestConfigurationRoundTripTests</c>'s own comment on why a unit test must never do that).
    /// </summary>
    internal static BacktestConfigurationLoadResult ValidateDeserializedConfiguration(
        BacktestConfigurationDto? dto, int maxConditionOffset, int? maxConditionTreeDepth = null, int? maxConditionTreeNodes = null, int? maxConditionGroupNameLength = null)
    {
        if (dto is null)
        {
            return BacktestConfigurationLoadResult.Error("backtest_configuration.json deserialized to null (empty or JSON \"null\").");
        }
        if (dto.SchemaVersion is not (BacktestConfigurationDto.CurrentSchemaVersion or BacktestConfigurationDto.ConditionTreeSchemaVersion))
        {
            return BacktestConfigurationLoadResult.Error(
                $"Unrecognized SchemaVersion {dto.SchemaVersion} (expected {BacktestConfigurationDto.CurrentSchemaVersion} or {BacktestConfigurationDto.ConditionTreeSchemaVersion}).");
        }
        if (!Enum.IsDefined(typeof(TimeFrame), dto.Frame))
        {
            return BacktestConfigurationLoadResult.Error($"Frame value {dto.Frame} is not a defined TimeFrame.");
        }
        if (!Enum.IsDefined(typeof(ExecutionModel), dto.ExecutionModel))
        {
            return BacktestConfigurationLoadResult.Error($"ExecutionModel value {dto.ExecutionModel} is not defined.");
        }
        if (!Enum.IsDefined(typeof(PositionSizingModel), dto.SizingModel))
        {
            return BacktestConfigurationLoadResult.Error($"SizingModel value {dto.SizingModel} is not a defined PositionSizingModel.");
        }
        if (dto.EvaluationStartUtc.Kind != DateTimeKind.Utc || dto.EvaluationEndUtc.Kind != DateTimeKind.Utc)
        {
            return BacktestConfigurationLoadResult.Error("EvaluationStartUtc and EvaluationEndUtc must have DateTimeKind.Utc.");
        }
        if (dto.EvaluationStartUtc > dto.EvaluationEndUtc)
        {
            return BacktestConfigurationLoadResult.Error("EvaluationStartUtc must be less than or equal to EvaluationEndUtc.");
        }
        if (dto.SelectedIndicators is null)
        {
            return BacktestConfigurationLoadResult.Error("SelectedIndicators must not be null.");
        }
        if (dto.ConditionEntries is null)
        {
            return BacktestConfigurationLoadResult.Error("ConditionEntries must not be null.");
        }

        try
        {
            var configuration = new BacktestConfiguration
            {
                ExecutionModel = dto.ExecutionModel,
                InitialCapital = dto.InitialCapital,
                CommissionFlat = dto.CommissionFlat,
                CommissionPerUnit = dto.CommissionPerUnit,
                SlippageRatio = dto.SlippageRatio,
                SizingModel = dto.SizingModel,
                SizingParameter = dto.SizingParameter,
                InitialMarginRatio = dto.InitialMarginRatio,
                MaintenanceMarginRatio = dto.MaintenanceMarginRatio,
                LiquidationPenaltyRatio = dto.LiquidationPenaltyRatio,
            };
            configuration.Validate();
            BacktestConditionValidator.ValidateRiskManagement(dto.RiskManagement is null ? null : new BacktestRiskManagementSettings
            {
                StopLossPercent = dto.RiskManagement.StopLossPercent,
                TakeProfitPercent = dto.RiskManagement.TakeProfitPercent,
            });
            _ = new Reporting.BacktestReportOptions(dto.Frame, 0, dto.EvaluationStartUtc, dto.EvaluationEndUtc)
            {
                BootstrapSeed = dto.BootstrapSeed,
                BootstrapIterations = dto.BootstrapIterations,
            };
        }
        catch (Exception ex) when (ex is ArgumentException or OverflowException)
        {
            return BacktestConfigurationLoadResult.Error(ex.Message);
        }

        for (int i = 0; i < dto.SelectedIndicators.Count; i++)
        {
            BacktestSelectedIndicatorDto? indicator = dto.SelectedIndicators[i];
            if (indicator is null)
            {
                return BacktestConfigurationLoadResult.Error($"SelectedIndicators[{i}] must not be null.");
            }
            if (!Enum.IsDefined(typeof(IndicatorType), indicator.Type))
            {
                return BacktestConfigurationLoadResult.Error($"SelectedIndicators contains an undefined IndicatorType value ({indicator.Type}) for key '{indicator.Key}'.");
            }
            if (indicator.Frame.HasValue && !Enum.IsDefined(typeof(TimeFrame), indicator.Frame.Value))
            {
                return BacktestConfigurationLoadResult.Error($"SelectedIndicators entry '{indicator.Key}' has an undefined Frame value ({indicator.Frame.Value}).");
            }
            BacktestIndicatorViolationReason selectedViolation = BacktestIndicatorEligibility.Check(indicator.Type, outputName: null);
            if (selectedViolation != BacktestIndicatorViolationReason.None)
            {
                return BacktestConfigurationLoadResult.Error(
                    $"SelectedIndicators entry '{indicator.Key}': {BacktestIndicatorEligibility.Describe(indicator.Type, null, selectedViolation)}");
            }
        }

        try
        {
            // Version 1: the flat list is validated exactly as before; version 2: the tree (source-of-truth rules and size limits included).
            _ = BacktestConditionTreeDtoMapper.ResolveTree(dto, maxConditionOffset, maxConditionTreeDepth, maxConditionTreeNodes, maxConditionGroupNameLength);
        }
        catch (Exception ex) when (ex is ArgumentException or OverflowException)
        {
            return BacktestConfigurationLoadResult.Error(ex.Message);
        }

        return BacktestConfigurationLoadResult.Loaded(dto);
    }

    public async Task SaveAsync(BacktestConfigurationDto configuration)
    {
        if (configuration is null) throw new ArgumentNullException(nameof(configuration));

        BacktestConfigurationDto snapshot = BacktestConfigurationSnapshot.Create(configuration);

        BacktestConfigurationLoadResult validation = ValidateDeserializedConfiguration(snapshot, _settings.BacktestMaxConditionOffset, _settings.BacktestMaxConditionTreeDepth, _settings.BacktestMaxConditionTreeNodes, _settings.BacktestMaxConditionGroupNameLength);
        if (validation.Status != BacktestConfigurationLoadStatus.Loaded)
        {
            throw new ArgumentException(validation.ErrorMessage ?? "Backtest configuration is invalid.", nameof(configuration));
        }

        await _semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            if (snapshot.SchemaVersion == BacktestConfigurationDto.ConditionTreeSchemaVersion)
            {
                await BackUpVersion1FileAsync().ConfigureAwait(false);
            }
            await AtomicJsonFile.SaveAsync(_filePath, snapshot, _jsonOptions).ConfigureAwait(false);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>Suffix appended to the configuration file name for the one-time copy of a version 1 file (see <see cref="BackUpVersion1FileAsync"/>).</summary>
    internal const string Version1BackupSuffix = ".v1.bak";

    /// <summary>
    /// Called (inside the semaphore, before the write) when a condition-tree file is about to replace the stored file: if that stored file is still
    /// version 1, it is copied once to <c>{file}.v1.bak</c>, because an older build cannot read the new file and the migration is one-way. An existing backup is never
    /// overwritten (the first, oldest version 1 content wins); a readable non-version-1 file is not backed up. A stored file whose version cannot be
    /// determined (unreadable, unparsable, empty) aborts the save, because whether a backup is required is unknown; so does a failed copy. Both throw an
    /// <see cref="IOException"/> and leave the original file untouched.
    /// </summary>
    private async Task BackUpVersion1FileAsync()
    {
        if (!File.Exists(_filePath)) return;

        BacktestConfigurationDto? stored;
        try
        {
            stored = await AtomicJsonFile.LoadAsync<BacktestConfigurationDto?>(_filePath, _jsonOptions).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new IOException($"The stored configuration '{_filePath}' could not be read to decide whether a version 1 backup is needed; the configuration was not saved.", ex);
        }
        if (stored is null)
        {
            throw new IOException($"The stored configuration '{_filePath}' has no recognizable content, so whether a version 1 backup is needed is unknown; the configuration was not saved.");
        }
        if (stored.SchemaVersion != BacktestConfigurationDto.CurrentSchemaVersion) return;

        string backupPath = _filePath + Version1BackupSuffix;
        if (File.Exists(backupPath)) return;

        // Copy to a temporary name first and move it into place: a crash mid-copy can never leave a torn file under the backup name (which the
        // "backup exists" check above would then keep forever). Move does not overwrite, so a backup created in between is kept as well.
        string temporaryPath = backupPath + ".tmp";
        try
        {
            File.Copy(_filePath, temporaryPath, overwrite: true);
            File.Move(temporaryPath, backupPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception cleanupEx) when (cleanupEx is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(cleanupEx, "The temporary backup file {TemporaryPath} could not be removed.", temporaryPath);
            }
            throw new IOException($"The version 1 backup '{backupPath}' could not be created; the configuration was not saved.", ex);
        }
    }
}
