using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using StockAnalyzer.Core.Models.Training;
using Xunit;

namespace StockAnalyzer.Core.Tests.Models.Training;

/// <summary>
/// The wizard's hyperparameter key list repeats the Python trainers' defaults. These tests read the trainer sources, so a renamed flag, a changed default
/// or a key that run_training reserves fails here instead of reaching a training run.
/// </summary>
public sealed class TrainingHyperparameterCatalogTests
{
    private static readonly IReadOnlyDictionary<TrainingFramework, string> TrainerFile = new Dictionary<TrainingFramework, string>
    {
        [TrainingFramework.PyTorch] = "train_pytorch.py",
        [TrainingFramework.TensorFlow] = "train_tensorflow.py",
        [TrainingFramework.LightGBM] = "train_lightgbm.py",
    };

    /// <summary>Key -> the module constant holding the trainer default, per source file (shared keys appear in several trainers).</summary>
    private static readonly (string File, string Key, string Constant)[] DefaultConstants =
    {
        ("train_pytorch.py", "hidden", "DEFAULT_HIDDEN"), ("train_pytorch.py", "layers", "DEFAULT_LAYERS"),
        ("train_pytorch.py", "dropout", "DEFAULT_DROPOUT"), ("train_pytorch.py", "epochs", "DEFAULT_EPOCHS"),
        ("train_pytorch.py", "batch", "DEFAULT_BATCH"), ("train_pytorch.py", "lr", "DEFAULT_LR"),
        ("train_pytorch.py", "weight-decay", "DEFAULT_WEIGHT_DECAY"), ("train_pytorch.py", "patience", "DEFAULT_PATIENCE"),
        ("train_pytorch.py", "seed", "DEFAULT_SEED"),
        ("train_tensorflow.py", "hidden", "DEFAULT_HIDDEN"), ("train_tensorflow.py", "dense", "DEFAULT_DENSE"),
        ("train_tensorflow.py", "dropout", "DEFAULT_DROPOUT"), ("train_tensorflow.py", "epochs", "DEFAULT_EPOCHS"),
        ("train_tensorflow.py", "batch", "DEFAULT_BATCH"), ("train_tensorflow.py", "lr", "DEFAULT_LR"),
        ("train_tensorflow.py", "patience", "DEFAULT_PATIENCE"), ("train_tensorflow.py", "seed", "DEFAULT_SEED"),
        ("train_lightgbm.py", "n-estimators", "DEFAULT_N_ESTIMATORS"), ("train_lightgbm.py", "learning-rate", "DEFAULT_LEARNING_RATE"),
        ("train_lightgbm.py", "num-leaves", "DEFAULT_NUM_LEAVES"), ("train_lightgbm.py", "max-depth", "DEFAULT_MAX_DEPTH"),
        ("train_lightgbm.py", "min-child-samples", "DEFAULT_MIN_CHILD_SAMPLES"), ("train_lightgbm.py", "subsample", "DEFAULT_SUBSAMPLE"),
        ("train_lightgbm.py", "colsample-bytree", "DEFAULT_COLSAMPLE"), ("train_lightgbm.py", "early-stopping", "DEFAULT_EARLY_STOPPING"),
        ("train_lightgbm.py", "seed", "DEFAULT_SEED"),
        ("dataset.py", "threshold", "DEFAULT_THRESHOLD"),
        ("dataset.py", "class-weight-power", "DEFAULT_CLASS_WEIGHT_POWER"),
        ("attention_export.py", "heads", "DEFAULT_HEADS"),
    };

    private static string Trainer(string file)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "StockAnalyzer.sln"))) directory = directory.Parent;
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory!.FullName, "StockAnalyzer.Python", "training", file));
    }

    private static double PythonDefault(string file, string constant)
    {
        var match = Regex.Match(Trainer(file), $@"^{constant}\s*:\s*\w+\s*=\s*(?<v>[-+0-9.eE]+)\s*(#.*)?$", RegexOptions.Multiline);
        Assert.True(match.Success, $"{file} has no numeric literal {constant}");
        return double.Parse(match.Groups["v"].Value, CultureInfo.InvariantCulture);
    }

    [Fact]
    public void EveryDefaultEqualsTheTrainersOwnConstant()
    {
        foreach (var definition in TrainingHyperparameterCatalog.All)
        {
            var sources = DefaultConstants.Where(c => c.Key == definition.Key).ToArray();
            Assert.NotEmpty(sources);
            foreach (var (file, key, constant) in sources)
                Assert.Equal(PythonDefault(file, constant), double.Parse(definition.DefaultValue, CultureInfo.InvariantCulture), 12);
        }
    }

    [Theory]
    [InlineData("epochs", true, "9223372036854775807")]       // integer without a maximum: the end of the 64-bit range that Validate applies
    [InlineData("seed", false, "0")]                          // its own minimum
    [InlineData("max-depth", false, "-1")]
    [InlineData("heads", true, "16")]
    [InlineData("dropout", true, "1")]
    [InlineData("learning-rate", true, "")]                   // real without a maximum: nothing to name
    public void LimitText_NamesTheBoundOfTheDomain(string key, bool upper, string expected)
    {
        var domain = TrainingHyperparameterCatalog.Find(key)!.Domain!;

        Assert.Equal(expected, TrainingHyperparameterCatalog.LimitText(domain, upper));
    }

    [Fact]
    public void EveryDefaultConstantOfTheTrainersIsListedForItsFramework()
    {
        // the other direction: a catalog key must be offered for every trainer file whose constant it mirrors
        foreach (var (file, key, _) in DefaultConstants.Where(c => TrainerFile.Values.Contains(c.File)))
        {
            var framework = TrainerFile.Single(f => f.Value == file).Key;
            Assert.Contains(TrainingHyperparameterCatalog.All, d => d.Key == key && d.Frameworks.Contains(framework));
        }
    }

    [Fact]
    public void EveryKeyIsAFlagOfEveryTrainerItIsOfferedFor_AndNoKeyIsReserved()
    {
        var reservedBlock = Regex.Match(Trainer("trainer_flags.py"), @"^RESERVED_TRAINER_FLAGS: frozenset = frozenset\(\{(?<body>.*?)\}\)", RegexOptions.Singleline | RegexOptions.Multiline);
        Assert.True(reservedBlock.Success);
        var reserved = Regex.Matches(reservedBlock.Groups["body"].Value, "\"([a-z0-9-]+)\"").Select(m => m.Groups[1].Value).ToHashSet();
        Assert.NotEmpty(reserved);

        foreach (var definition in TrainingHyperparameterCatalog.All)
        {
            Assert.DoesNotContain(definition.Key, reserved);
            foreach (var framework in definition.Frameworks)
                Assert.Contains($"\"--{definition.Key}\"", Trainer(TrainerFile[framework]));
        }
    }

    [Fact]
    public void KeysAreListedForTheirFrameworkAndArchitecture_AndHeadsOnlyForTheAttentionModel()
    {
        string[] Keys(TrainingFramework framework, string architecture) =>
            TrainingHyperparameterCatalog.For(framework, architecture).Select(d => d.Key).ToArray();

        Assert.Contains("lr", Keys(TrainingFramework.PyTorch, "lstm"));
        Assert.DoesNotContain("heads", Keys(TrainingFramework.PyTorch, "lstm"));
        Assert.Contains("heads", Keys(TrainingFramework.PyTorch, AttentionContract.Architecture));
        Assert.DoesNotContain("layers", Keys(TrainingFramework.TensorFlow, "gru"));
        Assert.Contains("dense", Keys(TrainingFramework.TensorFlow, "gru"));
        Assert.Contains("n-estimators", Keys(TrainingFramework.LightGBM, TrainingEnsembleContract.LightGbmArchitecture));
        Assert.DoesNotContain("epochs", Keys(TrainingFramework.LightGBM, TrainingEnsembleContract.LightGbmArchitecture));
        Assert.Equal(TrainingHyperparameterCatalog.All.Count, TrainingHyperparameterCatalog.All.Select(d => d.Key).Distinct().Count());
    }

    [Theory]
    [InlineData("weight_decay", "weight-decay")]
    [InlineData("--LR", "lr")]
    [InlineData(" Hidden ", "hidden")]
    public void FindUsesTheTrainerFlagNormalization(string typed, string expectedKey)
    {
        Assert.Equal(expectedKey, TrainingHyperparameterCatalog.Find(typed)?.Key);
    }

    // --- Value domains ---------------------------------------------------------------------------------------------------------------------

    public static IEnumerable<object[]> EveryKey() => TrainingHyperparameterCatalog.All.Select(d => new object[] { d.Key });

    [Fact]
    public void EveryCatalogEntry_HasADomainDecision()
    {
        // A new key cannot be added without deciding its domain; a key without a meaningful domain must say so with new(Kind).
        Assert.All(TrainingHyperparameterCatalog.All, d => Assert.NotNull(d.Domain));
    }

    [Theory]
    [MemberData(nameof(EveryKey))]
    public void TheTrainerDefault_IsInsideTheDomain(string key)
    {
        var definition = TrainingHyperparameterCatalog.Find(key)!;
        Assert.Equal(HyperparameterProblem.None, TrainingHyperparameterCatalog.Validate(key, definition.DefaultValue));
    }

    [Theory]
    [InlineData("dropout", "0", HyperparameterProblem.None)]
    [InlineData("dropout", "-0.0001", HyperparameterProblem.BelowMinimum)]
    [InlineData("dropout", "0.9999", HyperparameterProblem.None)]
    [InlineData("dropout", "1", HyperparameterProblem.AboveMaximum)]
    [InlineData("epochs", "0", HyperparameterProblem.BelowMinimum)]
    [InlineData("epochs", "1", HyperparameterProblem.None)]
    [InlineData("heads", "0", HyperparameterProblem.BelowMinimum)]
    [InlineData("heads", "16", HyperparameterProblem.None)]
    [InlineData("heads", "17", HyperparameterProblem.AboveMaximum)]
    [InlineData("subsample", "0", HyperparameterProblem.BelowMinimum)]
    [InlineData("subsample", "1", HyperparameterProblem.None)]
    [InlineData("subsample", "1.0001", HyperparameterProblem.AboveMaximum)]
    [InlineData("colsample-bytree", "0", HyperparameterProblem.BelowMinimum)]
    [InlineData("colsample-bytree", "1", HyperparameterProblem.None)]
    [InlineData("max-depth", "-2", HyperparameterProblem.BelowMinimum)]
    [InlineData("max-depth", "-1", HyperparameterProblem.None)]
    [InlineData("num-leaves", "1", HyperparameterProblem.BelowMinimum)]
    [InlineData("num-leaves", "2", HyperparameterProblem.None)]
    [InlineData("lr", "0", HyperparameterProblem.BelowMinimum)]
    [InlineData("lr", "0.0000001", HyperparameterProblem.None)]
    [InlineData("learning-rate", "-0.1", HyperparameterProblem.BelowMinimum)]
    [InlineData("weight-decay", "0", HyperparameterProblem.None)]
    [InlineData("weight-decay", "-1", HyperparameterProblem.BelowMinimum)]
    [InlineData("threshold", "0", HyperparameterProblem.None)]
    [InlineData("threshold", "-0.001", HyperparameterProblem.BelowMinimum)]
    [InlineData("seed", "0", HyperparameterProblem.None)]
    [InlineData("seed", "-1", HyperparameterProblem.BelowMinimum)]
    public void TheBoundaries_FollowTheInclusiveAndExclusiveFlags(string key, string value, HyperparameterProblem expected)
    {
        Assert.Equal(expected, TrainingHyperparameterCatalog.Validate(key, value));
    }

    [Theory]
    [InlineData("epochs", "abc", HyperparameterProblem.NotInteger)]
    [InlineData("epochs", "2.5", HyperparameterProblem.NotInteger)]
    [InlineData("epochs", "", HyperparameterProblem.NotInteger)]
    [InlineData("epochs", "  ", HyperparameterProblem.NotInteger)]
    [InlineData("epochs", "99999999999999999999", HyperparameterProblem.AboveMaximum)]
    [InlineData("max-depth", "-99999999999999999999", HyperparameterProblem.BelowMinimum)]
    [InlineData("lr", "abc", HyperparameterProblem.NotNumber)]
    [InlineData("lr", "", HyperparameterProblem.NotNumber)]
    [InlineData("lr", "NaN", HyperparameterProblem.NotFinite)]
    [InlineData("lr", "Infinity", HyperparameterProblem.NotFinite)]
    [InlineData("lr", "1e999", HyperparameterProblem.NotFinite)]
    public void ATextThatIsNotAFiniteNumberOfTheKind_IsRefused(string key, string value, HyperparameterProblem expected)
    {
        Assert.Equal(expected, TrainingHyperparameterCatalog.Validate(key, value));
    }

    [Fact]
    public void AnUnknownKey_AndASpellingOfAKnownKey_AreJudgedByTheTrainerFlagRules()
    {
        Assert.Equal(HyperparameterProblem.None, TrainingHyperparameterCatalog.Validate("not-a-flag", "anything"));
        Assert.Equal(HyperparameterProblem.BelowMinimum, TrainingHyperparameterCatalog.Validate("--Weight_Decay", "-1"));
        Assert.Equal(HyperparameterProblem.None, TrainingHyperparameterCatalog.Validate("epochs", " 5 "));
    }

    [Theory]
    [InlineData(AttentionContract.Architecture, "64", "3", HyperparameterProblem.HeadsDoNotDivideHidden)]
    [InlineData(AttentionContract.Architecture, "64", "4", HyperparameterProblem.None)]
    [InlineData(AttentionContract.Architecture, "6", null, HyperparameterProblem.HeadsDoNotDivideHidden)]   // heads falls back to the default 4
    [InlineData(AttentionContract.Architecture, null, "5", HyperparameterProblem.HeadsDoNotDivideHidden)]   // hidden falls back to the default 64
    [InlineData("lstm", "64", "3", HyperparameterProblem.None)]                          // other architectures are not judged
    public void TheAttentionArchitecture_NeedsHiddenToBeAMultipleOfHeads(string architecture, string? hidden, string? heads, HyperparameterProblem expected)
    {
        var values = new Dictionary<string, string>();
        if (hidden is not null) values["hidden"] = hidden;
        if (heads is not null) values["heads"] = heads;

        Assert.Equal(expected, TrainingHyperparameterCatalog.ValidateAttentionHeads(architecture, values));
    }

    [Fact]
    public void AnInvalidHeadsValue_UsesTheDefaultForTheDependency()
    {
        var values = new Dictionary<string, string> { ["hidden"] = "64", ["heads"] = "abc" };
        Assert.Equal(HyperparameterProblem.None, TrainingHyperparameterCatalog.ValidateAttentionHeads(AttentionContract.Architecture, values));
    }

    [Fact]
    public void FindDuplicates_NamesEachRepeatedFlagOnce_InFirstOccurrenceOrder()
    {
        Assert.Equal(new[] { "epochs" }, TrainingHyperparameterCatalog.FindDuplicates(new[] { "epochs", "--epochs", "EPOCHS", "lr" }));
        Assert.Empty(TrainingHyperparameterCatalog.FindDuplicates(new[] { "epochs", "lr" }));
        Assert.Equal(new[] { "lr", "epochs" }, TrainingHyperparameterCatalog.FindDuplicates(new[] { "lr", "epochs", "epochs", "LR" }));
        Assert.Equal(new[] { "weight-decay" }, TrainingHyperparameterCatalog.FindDuplicates(new[] { "weight_decay", "--weight-decay", "", "  " }));
    }

    [Fact]
    public void AnUnknownOrEmptyKeyHasNoDefinition()
    {
        Assert.Null(TrainingHyperparameterCatalog.Find("not-a-flag"));
        Assert.Null(TrainingHyperparameterCatalog.Find(""));
        Assert.Null(TrainingHyperparameterCatalog.Find(null));
    }
}
