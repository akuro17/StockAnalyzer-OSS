using System;
using System.Collections.Immutable;

namespace StockAnalyzer.Core.Models.ReinforcementLearning;

/// <summary>Terminal return of one validation block of one candidate (exact decimal, as the Core session computed it).</summary>
public sealed record RlPortfolioBlockReturn(string BlockId, decimal Return);

/// <summary>One complete validation candidate: a seed at a checkpoint, with its mean terminal return over every validation block.</summary>
public sealed record RlPortfolioCandidate(int Seed, int Checkpoint, decimal MeanReturn, ImmutableArray<RlPortfolioBlockReturn> BlockReturns);

/// <summary>Training transitions the Core session stepped for one seed.</summary>
public sealed record RlPortfolioSeedProgress(int Seed, int TrainingTransitions);

/// <summary>
/// Selection evidence of one portfolio run, written beside the model so the choice of the checkpoint can be audited without the UI. The cell the
/// checkpoint was selected on is stated; its returns are therefore optimistic.
/// </summary>
public sealed record RlPortfolioTrainingSummary(int SchemaVersion, string SpecificationId, Guid RunId, string SelectionRule, string SelectionPartition,
    string SelectionGroup, bool SmokeOnly, RlPortfolioCandidate Selected, ImmutableArray<RlPortfolioCandidate> Candidates,
    ImmutableArray<RlPortfolioSeedProgress> SeedProgress);
