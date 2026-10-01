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
/// Y:\Temp\sa_step_log_OnnxTrainingFoundation.md, 2026-08-29, "Empty train or val split")
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
}
