using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Backtest.Reporting;

namespace StockAnalyzer.McpServer.Catalog;

/// <summary>A settable property of an engine settings class that <c>sa_run_backtest</c> accepts, under <paramref name="Section"/>.<paramref name="Key"/>.</summary>
internal sealed record AllowedBacktestSetting(Type Owner, string Property, string Section, string Key);

/// <summary>A settable property of an engine settings class that <c>sa_run_backtest</c> does not accept, with the reason (never empty).</summary>
internal sealed record OmittedBacktestSetting(Type Owner, string Property, string Reason);

/// <summary>
/// The one classification of the settable properties of the engine settings classes the backtest tool maps (account, risk, report, and the run snapshot that carries the bootstrap values):
/// each is either accepted under a key or deliberately omitted with a reason. The binder derives its accepted keys from here, so a key has one
/// definition; <c>McpParityGuardTests</c> fails when a property is in both lists or in neither, which is how a setting added to the engine
/// cannot be forgotten silently (the same style as <see cref="Tools.McpSettingsExposure"/>).
/// </summary>
/// <remarks>
/// A reason that starts with <see cref="GapPrefix"/> says "the Backtest window has it, this tool does not accept it yet"; any other reason says
/// the omission is intended. The support document's coverage table is written from this prefix.
/// </remarks>
internal static class McpBacktestSettingsExposure
{
    public const string GapPrefix = "GAP:";

    public const string AccountSection = "account";
    public const string RiskSection = "riskManagement";
    public const string ReportSection = "report";

    // Report keys that set something other than a plain number of the report defaults: the daily entry of the annual period counts, and the two
    // bootstrap values the run snapshot carries (the window's settings page offers them with the report defaults).
    public const string AnnualPeriodsDailyKey = "annualPeriodsD1";
    public const string BootstrapSeedKey = "bootstrapSeed";
    public const string BootstrapIterationsKey = "bootstrapIterations";

    private const string SeparateArgumentReason = "Another argument of the tool (or the run itself) supplies it.";

    private const string NotInTheWindowReason = "The Backtest settings window does not offer it; the engine's default is used.";

    /// <summary>The classes whose settable properties are classified.</summary>
    public static IReadOnlyList<Type> Owners { get; } = new[]
    {
        typeof(BacktestConfiguration), typeof(BacktestRiskManagementSettings), typeof(BacktestReportDefaults), typeof(BacktestRunSnapshot),
    };

    /// <summary>Accepted settings in the order their keys are listed in error hints.</summary>
    public static IReadOnlyList<AllowedBacktestSetting> Allowed { get; } = new AllowedBacktestSetting[]
    {
        new(typeof(BacktestConfiguration), nameof(BacktestConfiguration.InitialCapital), AccountSection, "initialCapital"),
        new(typeof(BacktestConfiguration), nameof(BacktestConfiguration.CommissionFlat), AccountSection, "commissionFlat"),
        new(typeof(BacktestConfiguration), nameof(BacktestConfiguration.CommissionPerUnit), AccountSection, "commissionPerUnit"),
        new(typeof(BacktestConfiguration), nameof(BacktestConfiguration.SlippageRatio), AccountSection, "slippageRatio"),
        new(typeof(BacktestConfiguration), nameof(BacktestConfiguration.SizingModel), AccountSection, "sizingModel"),
        new(typeof(BacktestConfiguration), nameof(BacktestConfiguration.SizingParameter), AccountSection, "sizingParameter"),
        new(typeof(BacktestConfiguration), nameof(BacktestConfiguration.InitialMarginRatio), AccountSection, "initialMarginRatio"),
        new(typeof(BacktestConfiguration), nameof(BacktestConfiguration.MaintenanceMarginRatio), AccountSection, "maintenanceMarginRatio"),
        new(typeof(BacktestConfiguration), nameof(BacktestConfiguration.LiquidationPenaltyRatio), AccountSection, "liquidationPenaltyRatio"),
        new(typeof(BacktestRiskManagementSettings), nameof(BacktestRiskManagementSettings.StopLossPercent), RiskSection, "stopLossRatio"),
        new(typeof(BacktestRiskManagementSettings), nameof(BacktestRiskManagementSettings.TakeProfitPercent), RiskSection, "takeProfitRatio"),
        new(typeof(BacktestReportDefaults), nameof(BacktestReportDefaults.AnnualRiskFreeRate), ReportSection, "annualRiskFreeRate"),
        new(typeof(BacktestReportDefaults), nameof(BacktestReportDefaults.AnnualMAR), ReportSection, "annualMar"),
        // The tool runs the daily frame only, so of the per-frame period counts only the daily one can take effect.
        new(typeof(BacktestReportDefaults), nameof(BacktestReportDefaults.AnnualPeriodsByFrame), ReportSection, AnnualPeriodsDailyKey),
        new(typeof(BacktestRunSnapshot), nameof(BacktestRunSnapshot.BootstrapSeed), ReportSection, BootstrapSeedKey),
        new(typeof(BacktestRunSnapshot), nameof(BacktestRunSnapshot.BootstrapIterations), ReportSection, BootstrapIterationsKey),
    };

    public static IReadOnlyList<OmittedBacktestSetting> Omitted { get; } = new OmittedBacktestSetting[]
    {
        new(typeof(BacktestConfiguration), nameof(BacktestConfiguration.ExecutionModel), "The read-only gateway runs the Legacy execution model only."),
        new(typeof(BacktestConfiguration), nameof(BacktestConfiguration.TradingDaysPerYear), NotInTheWindowReason),
        new(typeof(BacktestConfiguration), nameof(BacktestConfiguration.ShortBorrowAnnualRate), NotInTheWindowReason),
        new(typeof(BacktestConfiguration), nameof(BacktestConfiguration.VolumeConstraintMode), NotInTheWindowReason),
        new(typeof(BacktestConfiguration), nameof(BacktestConfiguration.CorporateActionHandlingMode), NotInTheWindowReason),
        new(typeof(BacktestConfiguration), nameof(BacktestConfiguration.BorrowFeeBasisMode), NotInTheWindowReason),
        new(typeof(BacktestConfiguration), nameof(BacktestConfiguration.PricePathConflictMode), NotInTheWindowReason),
        new(typeof(BacktestConfiguration), nameof(BacktestConfiguration.MarginLiquidationDeficitMode), NotInTheWindowReason),
        new(typeof(BacktestConfiguration), nameof(BacktestConfiguration.PriceLimitFillMode), NotInTheWindowReason),
        new(typeof(BacktestConfiguration), nameof(BacktestConfiguration.MaxVolumeParticipationRatio), NotInTheWindowReason),
        new(typeof(BacktestConfiguration), nameof(BacktestConfiguration.IntrabarPathMode), NotInTheWindowReason),
        new(typeof(BacktestReportDefaults), nameof(BacktestReportDefaults.SchemaVersion), "The internal schema number of the saved settings."),
        new(typeof(BacktestRunSnapshot), nameof(BacktestRunSnapshot.Symbol), SeparateArgumentReason),
        new(typeof(BacktestRunSnapshot), nameof(BacktestRunSnapshot.Frame), SeparateArgumentReason),
        new(typeof(BacktestRunSnapshot), nameof(BacktestRunSnapshot.EvaluationStartUtc), SeparateArgumentReason),
        new(typeof(BacktestRunSnapshot), nameof(BacktestRunSnapshot.EvaluationEndUtc), SeparateArgumentReason),
        new(typeof(BacktestRunSnapshot), nameof(BacktestRunSnapshot.Configuration), SeparateArgumentReason),
        new(typeof(BacktestRunSnapshot), nameof(BacktestRunSnapshot.Conditions), SeparateArgumentReason),
        new(typeof(BacktestRunSnapshot), nameof(BacktestRunSnapshot.RiskManagement), SeparateArgumentReason),
        new(typeof(BacktestRunSnapshot), nameof(BacktestRunSnapshot.SelectedIndicators), SeparateArgumentReason),
        new(typeof(BacktestRunSnapshot), nameof(BacktestRunSnapshot.ReportDefaults), SeparateArgumentReason),
        new(typeof(BacktestRunSnapshot), nameof(BacktestRunSnapshot.ConditionTree), SeparateArgumentReason),
    };

    /// <summary>The accepted keys of one section, in table order (the argument's own key names, without the section).</summary>
    public static string[] KeysOf(string section)
        => Allowed.Where(setting => setting.Section == section).Select(setting => setting.Key).ToArray();
}
