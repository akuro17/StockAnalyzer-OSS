using System;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// How an <see cref="EnsembleSpec"/> is identified in prediction monitoring. Kept apart from the record so that the spec stays a plain value
/// (its own identity is <see cref="EnsembleSpec.Fingerprint"/>); the extension is public so that the presentation layer can use it.
/// </summary>
public static class EnsembleMonitoringIdentity
{
    /// <summary>Domain separator of <see cref="MonitoringId"/>; a new version string starts a new identity space.</summary>
    public const string MonitoringIdDomain = "ensemble-monitoring:v1";

    /// <summary>Separator of the parts of every hashed monitoring text of an ensemble: the id text and the ordered member hashes.</summary>
    internal const string MonitoringHashSeparator = "\n";

    /// <summary>
    /// The group id of this ensemble in prediction monitoring: 64 lower-case hexadecimal characters (the form the monitoring stores
    /// require of a model id), the SHA-256 of <see cref="MonitoringIdDomain"/>, a line feed and <see cref="EnsembleSpec.Fingerprint"/>. It follows
    /// every change of a member, weight or order, and the domain separation keeps it apart from the content hash of any model file.
    /// </summary>
    public static string MonitoringId(this EnsembleSpec spec) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(MonitoringIdDomain + MonitoringHashSeparator + spec.Fingerprint()))).ToLowerInvariant();
}
