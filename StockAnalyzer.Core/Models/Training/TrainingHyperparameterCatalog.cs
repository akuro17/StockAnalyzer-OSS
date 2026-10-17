using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace StockAnalyzer.Core.Models.Training;

/// <summary>Whether a hyperparameter value is a whole number or a real number.</summary>
public enum HyperparameterKind { Integer, Real }

/// <summary>
/// The mathematical value domain of a hyperparameter. A bound is null when there is none; <c>...Inclusive</c> says whether the bound itself is allowed.
/// The policy is to refuse only values that lie outside the mathematical domain and to stay permissive otherwise.
/// </summary>
public sealed record TrainingHyperparameterDomain(HyperparameterKind Kind, double? Minimum = null, bool MinimumInclusive = true,
    double? Maximum = null, bool MaximumInclusive = true);

/// <summary>Why a hyperparameter value (or a combination of values) cannot be passed to a trainer.</summary>
public enum HyperparameterProblem
{
    None,
    NotInteger,
    NotNumber,
    NotFinite,
    BelowMinimum,
    AboveMaximum,
    HeadsDoNotDivideHidden,
    DuplicateKey,
}

/// <summary>
/// One trainer hyperparameter the wizard offers in its key list: the key is the trainer's CLI flag without the dashes (see <c>run_training._build_trainer_argv</c>),
/// <see cref="DefaultValue"/> is the trainer's own default in invariant text. <see cref="Architecture"/> narrows a key to one architecture (null = every
/// architecture of the framework). <see cref="Domain"/> is the mathematical domain of the value (see <see cref="TrainingHyperparameterCatalog.Validate"/>).
/// </summary>
public sealed record TrainingHyperparameterDefinition(
    string Key, string DefaultValue, IReadOnlyList<TrainingFramework> Frameworks, string? Architecture = null,
    TrainingHyperparameterDomain? Domain = null)
{
    /// <summary>Suffix of the localization key of the tooltip (<c>TrainingWizard_Hp_&lt;Id&gt;</c>): the key with dashes as underscores.</summary>
    public string LocalizationId => Key.Replace('-', '_');
}

/// <summary>
/// The tunable trainer flags per framework / architecture. The defaults repeat the Python trainers' <c>DEFAULT_*</c> constants (those stay the source of
/// truth for what a run uses when a key is not given); <c>Tests/StockAnalyzer.Core.Tests/Models/Training/TrainingHyperparameterCatalogTests.cs</c> compares
/// every value with the trainer source, so a changed trainer default fails a test instead of drifting silently.
/// Flags the wizard sets through its own fields (window, horizon, splits, gap, architecture ...) are reserved in <c>run_training</c> and are not listed.
/// </summary>
public static class TrainingHyperparameterCatalog
{
    private static readonly TrainingFramework[] PyTorch = { TrainingFramework.PyTorch };
    private static readonly TrainingFramework[] TensorFlow = { TrainingFramework.TensorFlow };
    private static readonly TrainingFramework[] LightGbm = { TrainingFramework.LightGBM };
    private static readonly TrainingFramework[] Networks = { TrainingFramework.PyTorch, TrainingFramework.TensorFlow };
    private static readonly TrainingFramework[] AllFrameworks = { TrainingFramework.PyTorch, TrainingFramework.TensorFlow, TrainingFramework.LightGBM };

    /// <summary>Attention heads of <see cref="AttentionContract.Architecture"/> (mirrors <c>attention_export.DEFAULT_HEADS</c>).</summary>
    public const int DefaultAttentionHeads = 4;

    // Value domains. A count is at least 1, a rate is positive, a decay is non-negative, a fraction lies in (0, 1], a seed is a non-negative integer.
    // epochs / batch / patience (trainer rule > 0), heads (attention_export.validate_heads, MAX_HEADS) and dropout / threshold (train_meta rules) copy the trainers.
    private const double CountMinimum = 1;
    private const double LeavesMinimum = 2;       // a tree needs two leaves
    private const double NoLimitDepth = -1;       // LightGBM: -1 = no limit
    private const double Zero = 0;
    private const double One = 1;
    private static readonly TrainingHyperparameterDomain Count = new(HyperparameterKind.Integer, CountMinimum);
    private static readonly TrainingHyperparameterDomain PositiveReal = new(HyperparameterKind.Real, Zero, MinimumInclusive: false);
    private static readonly TrainingHyperparameterDomain NonNegativeReal = new(HyperparameterKind.Real, Zero);
    private static readonly TrainingHyperparameterDomain Fraction = new(HyperparameterKind.Real, Zero, MinimumInclusive: false, One);

    public static IReadOnlyList<TrainingHyperparameterDefinition> All { get; } = new[]
    {
        // Networks (PyTorch and TensorFlow)
        new TrainingHyperparameterDefinition("hidden", "64", Networks, Domain: Count),
        new TrainingHyperparameterDefinition("layers", "2", PyTorch, Domain: Count),
        new TrainingHyperparameterDefinition("heads", DefaultAttentionHeads.ToString(CultureInfo.InvariantCulture), PyTorch,
            AttentionContract.Architecture, new TrainingHyperparameterDomain(HyperparameterKind.Integer, CountMinimum, Maximum: AttentionContract.MaxHeads)),
        new TrainingHyperparameterDefinition("dense", "32", TensorFlow, Domain: Count),
        new TrainingHyperparameterDefinition("dropout", "0.2", Networks, Domain: new(HyperparameterKind.Real, Zero, Maximum: One, MaximumInclusive: false)),
        new TrainingHyperparameterDefinition("epochs", "15", Networks, Domain: Count),
        new TrainingHyperparameterDefinition("batch", "256", Networks, Domain: Count),
        new TrainingHyperparameterDefinition("lr", "0.001", Networks, Domain: PositiveReal),
        new TrainingHyperparameterDefinition("weight-decay", "0.0001", PyTorch, Domain: NonNegativeReal),
        new TrainingHyperparameterDefinition("patience", "4", Networks, Domain: Count),
        // LightGBM
        new TrainingHyperparameterDefinition("n-estimators", "200", LightGbm, Domain: Count),
        new TrainingHyperparameterDefinition("learning-rate", "0.05", LightGbm, Domain: PositiveReal),
        new TrainingHyperparameterDefinition("num-leaves", "31", LightGbm, Domain: new(HyperparameterKind.Integer, LeavesMinimum)),
        new TrainingHyperparameterDefinition("max-depth", "-1", LightGbm, Domain: new(HyperparameterKind.Integer, NoLimitDepth)),
        new TrainingHyperparameterDefinition("min-child-samples", "20", LightGbm, Domain: Count),
        new TrainingHyperparameterDefinition("subsample", "0.9", LightGbm, Domain: Fraction),
        new TrainingHyperparameterDefinition("colsample-bytree", "0.9", LightGbm, Domain: Fraction),
        new TrainingHyperparameterDefinition("early-stopping", "30", LightGbm, Domain: Count),
        // Every framework
        new TrainingHyperparameterDefinition("threshold", "0.005", AllFrameworks, Domain: NonNegativeReal),
        new TrainingHyperparameterDefinition("class-weight-power", "1", AllFrameworks, Domain: new(HyperparameterKind.Real, Zero, Maximum: One)),
        new TrainingHyperparameterDefinition("seed", "42", AllFrameworks, Domain: new(HyperparameterKind.Integer, Zero)),
    };

    /// <summary>The keys that apply to <paramref name="framework"/> with <paramref name="architecture"/>, in display order.</summary>
    public static IReadOnlyList<TrainingHyperparameterDefinition> For(TrainingFramework framework, string? architecture) =>
        All.Where(d => d.Frameworks.Contains(framework)
                       && (d.Architecture is null || string.Equals(d.Architecture, architecture, StringComparison.Ordinal))).ToArray();

    /// <summary>The definition of <paramref name="key"/> (case-insensitive, dashes and underscores equal, as the trainer flag is normalized), or null.</summary>
    public static TrainingHyperparameterDefinition? Find(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        var normalized = Normalize(key);
        return All.FirstOrDefault(d => string.Equals(d.Key, normalized, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Same rule as <c>run_training._normalize_flag</c>: trim, drop leading dashes, underscores become dashes.</summary>
    public static string Normalize(string key) => key.Trim().TrimStart('-').Replace('_', '-');

    /// <summary>Number format that reads back as the same value (a bound such as <c>0.005</c> is shown exactly as written in the catalog).</summary>
    private const string RoundTripFormat = "R";
    private const string HiddenKey = "hidden";
    private const string HeadsKey = "heads";
    private static readonly Regex WholeNumberText = new("^[+-]?[0-9]+$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Checks <paramref name="value"/> against the domain of <paramref name="key"/>. A key that is not in the catalog (or has no domain) is not judged
    /// (<see cref="HyperparameterProblem.None"/>): the trainer stays the authority for what it accepts. An empty value is not a number.
    /// An integer text that does not fit <see cref="long"/> is below the minimum when it starts with a minus sign, otherwise above the maximum.
    /// </summary>
    public static HyperparameterProblem Validate(string key, string? value)
    {
        if (Find(key)?.Domain is not { } domain) return HyperparameterProblem.None;
        var text = (value ?? string.Empty).Trim();
        double number;
        if (domain.Kind == HyperparameterKind.Integer)
        {
            if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var whole)) number = whole;
            else if (WholeNumberText.IsMatch(text)) return text.StartsWith('-') ? HyperparameterProblem.BelowMinimum : HyperparameterProblem.AboveMaximum;
            else return HyperparameterProblem.NotInteger;
        }
        else
        {
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number)) return HyperparameterProblem.NotNumber;
            if (!double.IsFinite(number)) return HyperparameterProblem.NotFinite;
        }

        if (domain.Minimum is { } minimum && (domain.MinimumInclusive ? number < minimum : number <= minimum)) return HyperparameterProblem.BelowMinimum;
        if (domain.Maximum is { } maximum && (domain.MaximumInclusive ? number > maximum : number >= maximum)) return HyperparameterProblem.AboveMaximum;
        return HyperparameterProblem.None;
    }

    /// <summary>
    /// The bound of <paramref name="domain"/> on the side given by <paramref name="upper"/> as invariant text for a message: its own bound, and for an integer
    /// domain without one the end of the 64-bit range that <see cref="Validate"/> applies to an integer text beyond <see cref="long"/>. Empty when there is
    /// no bound to name (a real-valued domain without that bound never yields a problem on that side).
    /// </summary>
    public static string LimitText(TrainingHyperparameterDomain domain, bool upper)
    {
        double? bound = upper ? domain.Maximum : domain.Minimum;
        if (bound is { } own) return own.ToString(RoundTripFormat, CultureInfo.InvariantCulture);
        if (domain.Kind != HyperparameterKind.Integer) return string.Empty;
        return (upper ? long.MaxValue : long.MinValue).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The rule of <c>attention_export.validate_heads</c>: for <see cref="AttentionContract.Architecture"/> the hidden size must be a multiple of the head
    /// count. A missing or invalid value is replaced by the catalog default, as the trainer would use its own default. Other architectures are not judged.
    /// </summary>
    public static HyperparameterProblem ValidateAttentionHeads(string? architecture, IReadOnlyDictionary<string, string> values)
    {
        if (!string.Equals(architecture, AttentionContract.Architecture, StringComparison.Ordinal)) return HyperparameterProblem.None;
        var (hidden, heads) = ResolveHiddenAndHeads(values);
        return hidden % heads != 0 ? HyperparameterProblem.HeadsDoNotDivideHidden : HyperparameterProblem.None;
    }

    /// <summary>The hidden size and head count the trainer would use for <paramref name="values"/> (valid entries, else the catalog defaults).</summary>
    public static (long Hidden, long Heads) ResolveHiddenAndHeads(IReadOnlyDictionary<string, string> values) =>
        (ResolveWhole(values, HiddenKey), ResolveWhole(values, HeadsKey));

    private static long ResolveWhole(IReadOnlyDictionary<string, string> values, string key)
    {
        foreach (var (name, text) in values)
            if (string.Equals(Normalize(name), key, StringComparison.OrdinalIgnoreCase) && Validate(key, text) == HyperparameterProblem.None)
                return long.Parse(text.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        return long.Parse(Find(key)!.DefaultValue, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The normalized keys (see <see cref="Normalize"/>, compared ignoring case) that occur more than once, each once, in first-occurrence order.
    /// <c>epochs</c>, <c>--epochs</c> and <c>EPOCHS</c> are one trainer flag. Blank keys are ignored.
    /// </summary>
    public static IReadOnlyList<string> FindDuplicates(IEnumerable<string> keys)
    {
        var order = new List<string>();
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in keys)
        {
            if (string.IsNullOrWhiteSpace(key)) continue;
            var normalized = Normalize(key);
            if (normalized.Length == 0) continue;
            if (counts.TryGetValue(normalized, out var seen)) counts[normalized] = seen + 1;
            else
            {
                counts[normalized] = 1;
                order.Add(normalized);
            }
        }

        var duplicates = order.Where(name => counts[name] > 1).ToList();
        return duplicates;
    }
}
