using System;
using System.Collections.Generic;
using System.Text.Json;

namespace StockAnalyzer.Core.Services
{
    /// <summary>
    /// Immutable result of inspecting the local Python environment: whether the embedded
    /// interpreter is present, its reported version, and the resolved version string of every
    /// queried distribution. A <c>null</c> entry in <see cref="PackageVersions"/> means the
    /// distribution is not installed; a missing key means it was not queried.
    /// </summary>
    public sealed record PythonEnvironmentSnapshot(
        bool InterpreterInstalled,
        string? PythonVersion,
        IReadOnlyDictionary<string, string?> PackageVersions)
    {
        private static readonly IReadOnlyDictionary<string, string?> EmptyPackages =
            new Dictionary<string, string?>();

        /// <summary>Shared snapshot for "interpreter not present / inspection unavailable".</summary>
        public static PythonEnvironmentSnapshot NotInstalled { get; } =
            new(false, null, EmptyPackages);

        /// <summary>
        /// Parses the JSON emitted by the inspection script, e.g.
        /// <c>{"python": "3.13.1", "packages": {"pandas": "2.2.2", "arch": null}}</c>.
        /// Any malformed input, or a missing/blank <c>python</c> value, yields
        /// <see cref="NotInstalled"/> rather than throwing, so a failed inspection can never crash
        /// the caller.
        /// </summary>
        public static PythonEnvironmentSnapshot Parse(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return NotInstalled;
            }

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    return NotInstalled;
                }

                string? pythonVersion =
                    root.TryGetProperty("python", out var pv) && pv.ValueKind == JsonValueKind.String
                        ? pv.GetString()
                        : null;

                if (string.IsNullOrWhiteSpace(pythonVersion))
                {
                    return NotInstalled;
                }

                var packages = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                if (root.TryGetProperty("packages", out var pkgs) && pkgs.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in pkgs.EnumerateObject())
                    {
                        packages[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                            ? prop.Value.GetString()
                            : null;
                    }
                }

                return new PythonEnvironmentSnapshot(true, pythonVersion, packages);
            }
            catch (JsonException)
            {
                return NotInstalled;
            }
        }
    }
}
