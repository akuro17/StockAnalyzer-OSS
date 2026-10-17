using System;

namespace StockAnalyzer.Core.Models.Training;

/// <summary>
/// Mirrors the empty-fold guard inside <c>dataset.walk_forward_split</c> (Python, unmodified by
/// this feature) so the Avalonia wizard can tell a user, before they click Start, how many raw
/// bars a single symbol's selected date range must contain for training to succeed at all
/// before any indicator warmup requirement.
/// </summary>
/// <remarks>
/// The trainers (<c>train_pytorch.py</c> / <c>train_lightgbm.py</c> / <c>train_tensorflow.py</c>)
/// build a expanding-window walk-forward split over the windowed samples of each symbol and
/// raise <c>SystemExit("Empty train or val split; ...")</c> when the last fold's test block
/// would be empty. That happened twice during manual verification of this feature (see
/// docs/sa_step_log_OnnxTrainingFoundation, 2026-08-29, "Empty train or val split")
/// with a selected date range that looked adequate by eye but wasn't - the fold-formation rule
/// is not just <c>window + horizon</c>. This class reproduces that rule in closed form (derived
/// and cross-checked against 55 direct calls to the real
/// <c>dataset.walk_forward_split</c> across randomized window/horizon pairs, including edge
/// cases down to window=horizon=1 - see the step log entry above) rather than duplicating the
/// Python loop, so the UI hint can update live as the user edits Window Size / Horizon without
/// spawning Python.
/// </remarks>
public static class WalkForwardDataRequirement
{
    /// <summary>
    /// Walk-forward split count the trainers use when <c>run_training.py</c> doesn't override it
    /// (it never does - <c>--wf-splits</c> is not part of the wizard's config surface). Mirrors
    /// <c>dataset.DEFAULT_WF_SPLITS</c>; keep the two in sync if that Python constant ever changes.
    /// </summary>
    public const int DefaultSplitCount = 5;

    /// <summary>
    /// Default forward label horizon in bars. Mirrors <c>dataset.DEFAULT_HORIZON</c>;
    /// keep in sync via <c>HyperparameterDomainParityTests.DefaultHorizon_MatchesPythonDatasetDefault</c>.
    /// </summary>
    public const int DefaultHorizon = 5;

    /// <summary>
    /// The purge margin the trainer applies: <paramref name="gap"/> when given, else <c>windowSize + horizon - 1</c> (mirrors <c>dataset.resolve_purge_gap</c>).
    /// </summary>
    public static int ResolveGap(int windowSize, int horizon, int? gap = null) => gap ?? checked(windowSize + horizon - 1);

    /// <summary>
    /// Minimum number of raw (unwindowed) bars a single symbol's filtered date range must
    /// contain before <c>walk_forward_split</c> can form at least one non-empty train/validation
    /// fold for the given <paramref name="windowSize"/> / <paramref name="horizon"/> /
    /// <paramref name="gap"/>.
    /// </summary>
    /// <param name="windowSize">Look-back length in bars. Must be positive.</param>
    /// <param name="horizon">Forward label horizon in bars. Must be positive.</param>
    /// <param name="splitCount">Walk-forward split count; defaults to <see cref="DefaultSplitCount"/>.</param>
    /// <param name="gap">
    /// Purge margin in bars <c>walk_forward_split</c> drops between each fold's train and test
    /// blocks. <see langword="null"/> uses the Python side's default of
    /// <paramref name="windowSize"/> + <paramref name="horizon"/> - 1 (the overlap a sliding
    /// window plus its forward label spans); mirrors <see cref="TrainingJobConfig.Gap"/>.
    /// Must be non-negative.
    /// </param>
    /// <param name="maxLag">Largest explicit causal lag in bars; zero when no lag is selected.</param>
    public static int MinimumRawBars(int windowSize, int horizon, int splitCount = DefaultSplitCount,
        int? gap = null, int maxLag = 0)
    {
        if (windowSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(windowSize), windowSize, "windowSize must be positive.");
        }
        if (horizon <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(horizon), horizon, "horizon must be positive.");
        }
        if (splitCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(splitCount), splitCount, "splitCount must be positive.");
        }
        if (maxLag < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxLag), maxLag, "maxLag cannot be negative.");
        }

        // Two quantities the closed form below needs; equal only in the default case, so they
        // must not be conflated once a caller overrides the gap:
        //   windowOffset - bars a sliding window plus its forward label span, i.e. the fixed
        //     difference between a symbol's raw bar count and its windowed-sample count
        //     (build_dataset: n_samples = raw - (window + horizon - 1)).
        //   purgeGap - the margin walk_forward_split drops between each fold's train/test blocks
        //     (dataset.py, resolved_gap = window + horizon - 1 when the caller passes none).
        long windowOffset = checked((long)windowSize + horizon - 1);
        long purgeGap = gap ?? windowOffset;
        if (purgeGap < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(gap), purgeGap, "gap cannot be negative.");
        }

        // fold_size = n_samples // (splitCount + 1); the last fold (k = splitCount - 1) needs
        // fold_size * splitCount + purgeGap < n_samples. Writing n_samples = (splitCount+1)*q + r
        // for r in [0, splitCount] (so fold_size = q exactly) turns that into q + r > purgeGap,
        // i.e. q >= purgeGap - r + 1, with q >= 1 also required (walk_forward_split returns no
        // folds at all when fold_size == 0). For r <= purgeGap the sample count decreases as r
        // grows; for r > purgeGap it increases. Its minimum is therefore at r=min(splitCount,
        // purgeGap), with no loop over a user-supplied split count.
        long remainder = Math.Min((long)splitCount, purgeGap);
        long foldSize = Math.Max(1L, checked(purgeGap - remainder + 1));
        long minSamples = checked(((long)splitCount + 1) * foldSize + remainder);

        // build_dataset skips candidate windows whose start is before maxLag. The fold count
        // therefore needs maxLag additional raw bars; it does not change the configured purge gap.
        long rawBars = checked(minSamples + windowOffset + maxLag);
        if (rawBars > int.MaxValue)
            throw new OverflowException("Required raw-bar history exceeds the supported count.");
        return checked((int)rawBars);
    }

    /// <summary>
    /// Raw bars one symbol needs for a whole training run, as <c>run_training.py</c> checks it before it starts a trainer:
    /// EVERY outer fold <c>0 .. splitCount - 1</c> must exist with a purged train side, and each outer train side must be
    /// carved again into a fit part and an inner validation part with the same split rule
    /// (<c>dataset.independent_fold_data(..., inner=True)</c>). The release fold carves one more segment (calibration, or the
    /// stacking meta segment) when <paramref name="calibrationSegment"/> is set. <see cref="MinimumRawBars"/> only guarantees
    /// that the last outer fold is not empty, which is far less than this (a trainer then stops with "no usable inner
    /// validation fold").
    /// </summary>
    /// <remarks>
    /// The result is the smallest count that succeeds together with every larger count (the feasibility is not monotone
    /// near the boundary because of integer division). It is a lower bound for ONE symbol with an unbroken history:
    /// several symbols are purged against the earliest start of any of them, and a True High/Low channel reads one more
    /// bar. Verified against the real Python function by <c>Tests/TrainingRequirementParity.json</c>.
    /// </remarks>
    /// <exception cref="OverflowException">The requirement exceeds <see cref="int.MaxValue"/> bars.</exception>
    public static int MinimumRawBarsForTraining(int windowSize, int horizon, int splitCount = DefaultSplitCount,
        int? gap = null, int maxLag = 0, bool calibrationSegment = false)
    {
        if (windowSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(windowSize), windowSize, "windowSize must be positive.");
        if (horizon <= 0)
            throw new ArgumentOutOfRangeException(nameof(horizon), horizon, "horizon must be positive.");
        if (splitCount <= 0 || splitCount > TrainingResourceOverrides.MaximumEvaluationFolds)
            throw new ArgumentOutOfRangeException(nameof(splitCount), splitCount,
                $"splitCount must be in 1..{TrainingResourceOverrides.MaximumEvaluationFolds}.");
        if (maxLag < 0)
            throw new ArgumentOutOfRangeException(nameof(maxLag), maxLag, "maxLag cannot be negative.");

        // The whole-run requirement is never smaller than the last-fold one, so an over-limit input is rejected here
        // before the search below (which walks down from a far larger count) is entered.
        _ = MinimumRawBars(windowSize, horizon, splitCount, gap, maxLag);

        long windowOffset = checked((long)windowSize + horizon - 1);
        long purgeGap = gap ?? windowOffset;
        // A sample whose label ends before the first feature bar of a later block may be fitted: the purge depth in samples.
        long depth = checked(windowOffset + maxLag);
        long blocks = splitCount + 1L;

        // From this sample count on every count succeeds (sufficient conditions, monotone in the count): each carve needs
        // a block size above the gap and a non-empty purged head; the outer train side is the first block minus the excess
        // of the purge depth over the gap; a second carve (calibration) needs the first carve's head to be large enough.
        long excess = Math.Max(0, depth - purgeGap);
        long carveBlock = Math.Max(purgeGap + 1, excess / splitCount + 1);
        long outerNeeded = checked(blocks * carveBlock);
        if (calibrationSegment)
        {
            long headNeeded = checked((outerNeeded + excess + splitCount - 1) / splitCount);
            outerNeeded = checked(blocks * Math.Max(carveBlock, headNeeded));
        }
        long guaranteed = checked(blocks * checked(outerNeeded + excess));

        // Scan down from the guaranteed count to the first count that fails; skip a whole block of counts when it
        // is feasible and the block size exceeds the gap (the remainder then cannot matter).
        // Feasible(s) depends on s only through block = floor(s/(K+1)) and, for the last outer fold, through testEnd = s.
        // When block > purgeGap, testStart = block*K + purgeGap < block*(K+1) <= s, so the last-fold test is non-empty for
        // every remainder and Feasible is constant on the whole block; the jump to block*(K+1) - 1 is therefore exact.
        long samples = guaranteed;
        while (samples > 0)
        {
            bool feasible = TrainingRunFeasible(samples, splitCount, purgeGap, depth, calibrationSegment);
            if (!feasible)
                break;
            long block = samples / blocks;
            samples = block > purgeGap ? block * blocks - 1 : samples - 1;
        }

        // samples is the largest infeasible count; samples + 1 is the smallest count from which every larger count is feasible;
        // windowOffset = W + H - 1 converts samples to raw bars; maxLag raw bars precede the first usable window.
        long rawBars = checked(samples + 1 + windowOffset + maxLag);
        if (rawBars > int.MaxValue)
            throw new OverflowException("Required raw-bar history exceeds the supported count.");
        return (int)rawBars;
    }

    /// <summary>Mirror of the per-symbol part of <c>dataset.independent_fold_data(inner=True)</c> for every outer fold.</summary>
    internal static bool TrainingRunFeasible(long samples, int splitCount, long gap, long depth, bool calibrationSegment)
    {
        long block = samples / (splitCount + 1L);
        if (block == 0)
            return false;
        for (int fold = 0; fold < splitCount; fold++)
        {
            long testStart = block * (fold + 1) + gap;
            long testEnd = fold == splitCount - 1 ? samples : block * (fold + 2);
            if (testStart >= testEnd)
                return false;
            long length = Math.Min(block * (fold + 1), Math.Max(0, testStart - depth));
            int carves = calibrationSegment && fold == splitCount - 1 ? 2 : 1;
            for (int carve = 0; carve < carves && length > 0; carve++)
                length = CarvedHead(length, splitCount, gap, depth);
            if (length <= 0)
                return false;
        }

        return true;
    }

    /// <summary>Length of the purged head left when a train side is split by its last existing walk-forward fold; 0 when there is none.</summary>
    private static long CarvedHead(long length, int splitCount, long gap, long depth)
    {
        long block = length / (splitCount + 1L);
        if (block == 0)
            return 0;
        long testStart = -1;
        for (int fold = 0; fold < splitCount; fold++)
        {
            long start = block * (fold + 1) + gap;
            long end = fold == splitCount - 1 ? length : block * (fold + 2);
            if (start < end)
                testStart = start;
        }

        return testStart < 0 ? 0 : Math.Min(testStart - gap, Math.Max(0, testStart - depth));
    }
}
