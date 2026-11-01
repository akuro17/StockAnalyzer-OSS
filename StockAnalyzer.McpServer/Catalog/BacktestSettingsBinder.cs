using System.Text.Json;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Configuration;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Reporting;
using StockAnalyzer.McpServer.Tools;
using static StockAnalyzer.McpServer.Catalog.ConditionJson;

namespace StockAnalyzer.McpServer.Catalog;

/// <summary>The account, risk and report settings of one run, with the effective values the result echoes.</summary>
internal sealed record BoundBacktestSettings(
    BacktestConfiguration Configuration,
    BacktestRiskManagementSettings? RiskManagement,
    BacktestReportDefaults ReportDefaults,
    int BootstrapSeed,
    int BootstrapIterations);

/// <summary>
/// Maps the optional <c>account</c>, <c>riskManagement</c> and <c>report</c> objects onto the engine's own configuration types.
/// Omitted values are the factory defaults of the Backtest window (<see cref="BacktestDefaults"/>, <see cref="BacktestReportDefaults.BuiltIn"/>);
/// the values the user saved in the window are not read (they live in files whose readers can create folders). Every value is checked by the
/// engine's own rules (the property bounds, <see cref="BacktestConfiguration.Validate"/>, <see cref="BacktestConditionValidator.ValidateRiskManagement"/>).
/// </summary>
internal static class BacktestSettingsBinder
{
    public const string AccountArgument = McpBacktestSettingsExposure.AccountSection;
    public const string RiskArgument = McpBacktestSettingsExposure.RiskSection;
    public const string ReportArgument = McpBacktestSettingsExposure.ReportSection;

    private const string AnnualPeriodsDailyKey = McpBacktestSettingsExposure.AnnualPeriodsDailyKey;
    private const string BootstrapSeedKey = McpBacktestSettingsExposure.BootstrapSeedKey;
    private const string BootstrapIterationsKey = McpBacktestSettingsExposure.BootstrapIterationsKey;

    private static readonly string[] AccountKeys = McpBacktestSettingsExposure.KeysOf(McpBacktestSettingsExposure.AccountSection);
    private static readonly string[] RiskKeys = McpBacktestSettingsExposure.KeysOf(McpBacktestSettingsExposure.RiskSection);
    private static readonly string[] ReportKeys = McpBacktestSettingsExposure.KeysOf(McpBacktestSettingsExposure.ReportSection);

    public static BoundBacktestSettings Bind(JsonElement arguments, TimeFrame frame = TimeFrame.D1)
    {
        BacktestConfiguration account = BindAccount(Object(arguments, AccountArgument));
        BacktestRiskManagementSettings? risk = BindRisk(Object(arguments, RiskArgument));
        (BacktestReportDefaults report, int seed, int iterations) = BindReport(Object(arguments, ReportArgument), frame);
        return new BoundBacktestSettings(account, risk, report, seed, iterations);
    }

    private static JsonElement? Object(JsonElement arguments, string name)
        => arguments.TryGetProperty(name, out JsonElement value) ? value : null;

    private static BacktestConfiguration BindAccount(JsonElement? account)
    {
        decimal Number(string key, decimal fallback) => ReadNumber(account, AccountArgument, key) ?? fallback;
        if (account is { } given)
        {
            RequireObject(given, AccountArgument);
            RejectUnknownKeys(given, AccountArgument, AccountKeys);
        }

        PositionSizingModel sizing = account is { } owner && owner.TryGetProperty("sizingModel", out JsonElement model)
            ? ParseEnum<PositionSizingModel>(model.ValueKind == JsonValueKind.String ? model : throw NotText($"{AccountArgument}.sizingModel"), $"{AccountArgument}.sizingModel", Enum.GetNames<PositionSizingModel>())
            : BacktestDefaults.SizingModel;

        try
        {
            var configuration = new BacktestConfiguration
            {
                ExecutionModel = ExecutionModel.Legacy,
                InitialCapital = Number("initialCapital", BacktestDefaults.InitialCapital),
                CommissionFlat = Number("commissionFlat", BacktestDefaults.CommissionFlat),
                CommissionPerUnit = Number("commissionPerUnit", BacktestDefaults.CommissionPerUnit),
                SlippageRatio = Number("slippageRatio", BacktestDefaults.SlippageRatio),
                SizingModel = sizing,
                SizingParameter = Number("sizingParameter", BacktestDefaults.SizingParameter),
                InitialMarginRatio = Number("initialMarginRatio", BacktestDefaults.InitialMarginRatio),
                MaintenanceMarginRatio = Number("maintenanceMarginRatio", BacktestDefaults.MaintenanceMarginRatio),
                LiquidationPenaltyRatio = Number("liquidationPenaltyRatio", BacktestDefaults.LiquidationPenaltyRatio),
            };
            configuration.Validate();
            return configuration;
        }
        catch (ArgumentOutOfRangeException error)
        {
            string key = error.ParamName is { Length: > 0 } name ? char.ToLowerInvariant(name[0]) + name[1..] : string.Empty;
            throw new ToolException(
                ToolErrorCodes.InvalidArgument,
                error.Message.Split(Environment.NewLine)[0],
                key.Length == 0 ? AccountArgument : $"{AccountArgument}.{key}",
                "Omit the value to use the Backtest window's factory default.");
        }
    }

    private static BacktestRiskManagementSettings? BindRisk(JsonElement? risk)
    {
        if (risk is not { } given)
        {
            return null;
        }

        RequireObject(given, RiskArgument);
        RejectUnknownKeys(given, RiskArgument, RiskKeys);
        decimal? stopLoss = ReadNumber(risk, RiskArgument, "stopLossRatio");
        decimal? takeProfit = ReadNumber(risk, RiskArgument, "takeProfitRatio");
        CheckRatio(stopLoss, "stopLossRatio");
        CheckRatio(takeProfit, "takeProfitRatio");

        DynamicExitConfiguration? dynamicExits = null;
        if (given.TryGetProperty("dynamicExits", out JsonElement dynamicExitsElement))
        {
            ConditionJson.RequireObject(dynamicExitsElement, $"{RiskArgument}.dynamicExits");
            dynamicExits = BindDynamicExits(dynamicExitsElement, RiskArgument);
        }

        var settings = new BacktestRiskManagementSettings
        {
            StopLossPercent = stopLoss,
            TakeProfitPercent = takeProfit,
            DynamicExits = dynamicExits,
        };
        BacktestConditionValidator.ValidateRiskManagement(settings);
        return settings.IsEnabled ? settings : null;
    }

    private static readonly string[] DynamicExitKeys = { "maxHoldingBars", "atrStop", "atrTarget", "trailingStop" };
    private static readonly string[] AtrExitKeys = { "period", "multiple" };
    private static readonly string[] TrailingExitKeys = { "mode", "period", "multiple", "ratio" };

    private static DynamicExitConfiguration? BindDynamicExits(JsonElement? element, string objectName)
    {
        if (element is not { ValueKind: JsonValueKind.Object } given)
        {
            return null;
        }

        string path = $"{objectName}.dynamicExits";
        ConditionJson.RejectUnknownKeys(given, path, DynamicExitKeys);

        int? maxHoldingBars = ReadInteger(given, path, "maxHoldingBars");
        if (maxHoldingBars is < 1)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, "'maxHoldingBars' must be >= 1.", $"{path}.maxHoldingBars");
        }

        AtrExitParameters? atrStop = null;
        if (given.TryGetProperty("atrStop", out JsonElement atrStopElement))
        {
            string p = $"{path}.atrStop";
            ConditionJson.RequireObject(atrStopElement, p);
            ConditionJson.RejectUnknownKeys(atrStopElement, p, AtrExitKeys);
            int period = ReadInteger(atrStopElement, p, "period") ?? throw new ToolException(ToolErrorCodes.InvalidArgument, "'period' is required.", $"{p}.period");
            if (period < 1) throw new ToolException(ToolErrorCodes.InvalidArgument, "'period' must be >= 1.", $"{p}.period");
            decimal multiple = ReadNumber(atrStopElement, p, "multiple") ?? throw new ToolException(ToolErrorCodes.InvalidArgument, "'multiple' is required.", $"{p}.multiple");
            if (multiple <= 0m) throw new ToolException(ToolErrorCodes.InvalidArgument, "'multiple' must be > 0.", $"{p}.multiple");
            atrStop = new AtrExitParameters { Period = period, Multiple = multiple };
        }

        AtrExitParameters? atrTarget = null;
        if (given.TryGetProperty("atrTarget", out JsonElement atrTargetElement))
        {
            string p = $"{path}.atrTarget";
            ConditionJson.RequireObject(atrTargetElement, p);
            ConditionJson.RejectUnknownKeys(atrTargetElement, p, AtrExitKeys);
            int period = ReadInteger(atrTargetElement, p, "period") ?? throw new ToolException(ToolErrorCodes.InvalidArgument, "'period' is required.", $"{p}.period");
            if (period < 1) throw new ToolException(ToolErrorCodes.InvalidArgument, "'period' must be >= 1.", $"{p}.period");
            decimal multiple = ReadNumber(atrTargetElement, p, "multiple") ?? throw new ToolException(ToolErrorCodes.InvalidArgument, "'multiple' is required.", $"{p}.multiple");
            if (multiple <= 0m) throw new ToolException(ToolErrorCodes.InvalidArgument, "'multiple' must be > 0.", $"{p}.multiple");
            atrTarget = new AtrExitParameters { Period = period, Multiple = multiple };
        }

        TrailingExitParameters? trailingStop = null;
        if (given.TryGetProperty("trailingStop", out JsonElement trailingElement))
        {
            string p = $"{path}.trailingStop";
            ConditionJson.RequireObject(trailingElement, p);
            ConditionJson.RejectUnknownKeys(trailingElement, p, TrailingExitKeys);
            if (!trailingElement.TryGetProperty("mode", out JsonElement modeElement) || modeElement.ValueKind != JsonValueKind.String)
            {
                throw new ToolException(ToolErrorCodes.InvalidArgument, "'mode' is required and must be a string.", $"{p}.mode");
            }
            TrailingExitMode mode = ConditionJson.ParseEnum<TrailingExitMode>(modeElement, $"{p}.mode", Enum.GetNames<TrailingExitMode>());
            int? period = ReadInteger(trailingElement, p, "period");
            decimal? multiple = ReadNumber(trailingElement, p, "multiple");
            decimal? ratio = ReadNumber(trailingElement, p, "ratio");
            if (mode == TrailingExitMode.AtrMultiple)
            {
                if (trailingElement.TryGetProperty("ratio", out _)) throw new ToolException(ToolErrorCodes.InvalidArgument, "'ratio' is not allowed for AtrMultiple mode.", $"{p}.ratio");
                if (period is null or < 1) throw new ToolException(ToolErrorCodes.InvalidArgument, "'period' must be >= 1 for AtrMultiple mode.", $"{p}.period");
                if (multiple is null or <= 0m) throw new ToolException(ToolErrorCodes.InvalidArgument, "'multiple' must be > 0 for AtrMultiple mode.", $"{p}.multiple");
            }
            else if (mode == TrailingExitMode.Ratio)
            {
                if (trailingElement.TryGetProperty("period", out _)) throw new ToolException(ToolErrorCodes.InvalidArgument, "'period' is not allowed for Ratio mode.", $"{p}.period");
                if (trailingElement.TryGetProperty("multiple", out _)) throw new ToolException(ToolErrorCodes.InvalidArgument, "'multiple' is not allowed for Ratio mode.", $"{p}.multiple");
                if (ratio is null or <= 0m or >= 1m) throw new ToolException(ToolErrorCodes.InvalidArgument, "'ratio' must satisfy 0 < ratio < 1 for Ratio mode.", $"{p}.ratio");
            }
            trailingStop = new TrailingExitParameters
            {
                Mode = mode,
                Period = period,
                Multiple = multiple,
                Ratio = ratio,
            };
        }

        var config = new DynamicExitConfiguration
        {
            MaxHoldingBars = maxHoldingBars,
            AtrStop = atrStop,
            AtrTarget = atrTarget,
            TrailingStop = trailingStop,
        };

        try
        {
            BacktestConditionValidator.ValidateDynamicExits(config);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, ex.Message.Split(Environment.NewLine)[0], path);
        }

        return config.IsEnabled ? config : null;
    }

    /// <summary>
    /// The risk settings a saved template carries, under the same rules as the <c>riskManagement</c> argument. The template stores the ratio of the
    /// entry price in the same field the argument fills (0.05 = 5%).
    /// </summary>
    public static BacktestRiskManagementSettings? FromTemplate(BacktestRiskManagementSettingsDto? risk)
    {
        if (risk is null)
        {
            return null;
        }

        CheckRatio(risk.StopLossPercent, "stopLossRatio");
        CheckRatio(risk.TakeProfitPercent, "takeProfitRatio");
        var settings = new BacktestRiskManagementSettings { StopLossPercent = risk.StopLossPercent, TakeProfitPercent = risk.TakeProfitPercent };
        BacktestConditionValidator.ValidateRiskManagement(settings);
        return settings.IsEnabled ? settings : null;
    }

    private static void CheckRatio(decimal? value, string key)
    {
        if (value is < 0m or >= 1m)
        {
            throw new ToolException(
                ToolErrorCodes.InvalidArgument,
                $"{key} must satisfy 0 <= ratio < 1.",
                $"{RiskArgument}.{key}",
                "A ratio of the entry price: 0.05 means 5%.");
        }
    }

    private static (BacktestReportDefaults Defaults, int BootstrapSeed, int BootstrapIterations) BindReport(JsonElement? report, TimeFrame frame)
    {
        if (report is { } given)
        {
            RequireObject(given, ReportArgument);
            RejectUnknownKeys(given, ReportArgument, ReportKeys);
        }

        BacktestReportDefaults builtIn = BacktestReportDefaults.BuiltIn;

        Dictionary<TimeFrame, int> periods = new(builtIn.AnnualPeriodsByFrame);
        if (ReadInteger(report, ReportArgument, AnnualPeriodsDailyKey) is { } daily)
        {
            periods[TimeFrame.D1] = daily;
        }

        int seed = ReadInteger(report, ReportArgument, BootstrapSeedKey) ?? BacktestDefaults.BootstrapSeed;
        int iterations = ReadInteger(report, ReportArgument, BootstrapIterationsKey) ?? BacktestDefaults.BootstrapIterations;
        try
        {
            // The engine's own rule for the resample count (the window checks it the same way); the other values are checked by the report defaults.
            DateTime placeholderUtc = DateTime.UtcNow;
            _ = new BacktestReportOptions(frame, 0, placeholderUtc, placeholderUtc) { BootstrapIterations = iterations };
        }
        catch (ArgumentOutOfRangeException error)
        {
            throw new ToolException(
                ToolErrorCodes.InvalidArgument,
                error.Message.Split(Environment.NewLine)[0],
                $"{ReportArgument}.{BootstrapIterationsKey}",
                "Omit the value to use the Backtest window's factory default.");
        }

        try
        {
            BacktestReportDefaults defaults = BacktestReportDefaults.SnapshotValidated(new BacktestReportDefaults
            {
                SchemaVersion = BacktestReportDefaults.CurrentSchemaVersion,
                AnnualRiskFreeRate = ReadNumber(report, ReportArgument, "annualRiskFreeRate") ?? builtIn.AnnualRiskFreeRate,
                AnnualMAR = ReadNumber(report, ReportArgument, "annualMar") ?? builtIn.AnnualMAR,
                AnnualPeriodsByFrame = periods,
            });
            return (defaults, seed, iterations);
        }
        catch (ArgumentOutOfRangeException error)
        {
            throw new ToolException(
                ToolErrorCodes.InvalidArgument,
                error.Message.Split(Environment.NewLine)[0],
                ReportArgument,
                "Annual rates are simple rates greater than -1 (0.02 means 2%); annualPeriodsD1 is the number of daily bars in a year.");
        }
    }

    private static int? ReadInteger(JsonElement? owner, string objectName, string key)
    {
        if (owner is not { ValueKind: JsonValueKind.Object } given || !given.TryGetProperty(key, out JsonElement value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int number))
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"'{key}' must be a whole number.", $"{objectName}.{key}");
        }

        return number;
    }

    private static decimal? ReadNumber(JsonElement? owner, string objectName, string key)
    {
        if (owner is not { ValueKind: JsonValueKind.Object } given || !given.TryGetProperty(key, out JsonElement value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out decimal number))
        {
            throw new ToolException(ToolErrorCodes.InvalidArgument, $"'{key}' must be a number.", $"{objectName}.{key}");
        }

        return number;
    }

    private static ToolException NotText(string path)
        => new(ToolErrorCodes.InvalidArgument, "The value must be a text.", path);
}
