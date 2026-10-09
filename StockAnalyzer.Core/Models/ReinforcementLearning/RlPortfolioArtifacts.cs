using System;
using System.Collections.Immutable;

namespace StockAnalyzer.Core.Models.ReinforcementLearning;

/// <summary>
/// Completion marker of one portfolio run, written last and atomically. <see cref="Files"/> maps every published file name
/// (directly inside the run folder) to its SHA-256, so a later listing can prove the evidence is intact.
/// </summary>
public sealed record RlPortfolioArtifactManifest(int SchemaVersion, string SpecificationId, Guid RunId, DateTimeOffset CreatedUtc,
    string UniverseSha256, string CalendarSha256, ImmutableSortedDictionary<string, string> Files);

/// <summary>Verified = complete and every listed file matches; Incomplete = no manifest yet (running, failed or interrupted run);
/// Invalid = the manifest cannot be trusted; Corrupt = a listed file is missing, linked or changed.</summary>
public enum RlPortfolioArtifactStatus { Verified, Incomplete, Invalid, Corrupt }

public sealed record RlPortfolioArtifactEntry(Guid RunId, string Directory, RlPortfolioArtifactStatus Status,
    RlPortfolioArtifactManifest? Manifest, string? Problem);
