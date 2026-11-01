using System;
using System.Text.Json;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.ReinforcementLearning;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>
/// SSoT helper for resolving and validating registered model identifiers in MCP tools.
/// Enforces path traversal prevention and uniform active/generation resolution.
/// </summary>
internal static class RegisteredModelResolver
{
    public const string ModelIdArgument = "modelId";

    public readonly record struct ResolvedModel(string RequestedId, string ResolvedId, ModelGenerationManifest Manifest);

    public static ResolvedModel Resolve(
        ReadOnlyModelGenerationRegistry registry,
        JsonElement arguments,
        string parameterName = ModelIdArgument)
    {
        ArgumentNullException.ThrowIfNull(registry);

        string? raw = ToolArgs.String(arguments, parameterName)?.Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new ToolException(
                ToolErrorCodes.InvalidArgument,
                $"Argument '{parameterName}' is required.",
                parameterName);
        }

        if (!PlainFileName.IsValid(raw) || raw.StartsWith('.'))
        {
            throw new ToolException(
                ToolErrorCodes.InvalidArgument,
                $"Argument '{parameterName}' must be a valid model identifier without path separators or parent segments.",
                parameterName,
                "Use a valid model ID or 'active'.");
        }

        if (string.Equals(raw, "active", StringComparison.OrdinalIgnoreCase))
        {
            string? activeId = registry.ActiveId;
            if (string.IsNullOrWhiteSpace(activeId))
            {
                throw new ToolException(
                    ToolErrorCodes.ArtifactNotFound,
                    "No model is currently active in the registry.",
                    parameterName);
            }

            ModelGenerationManifest activeManifest = registry.TryGetManifest(activeId)
                ?? throw new ToolException(
                    ToolErrorCodes.ArtifactNotFound,
                    $"Active model '{activeId}' was not found in the registry.",
                    parameterName);

            return new ResolvedModel(raw, activeManifest.ModelId, activeManifest);
        }

        ModelGenerationManifest manifest = registry.TryGetManifest(raw)
            ?? throw new ToolException(
                ToolErrorCodes.ArtifactNotFound,
                $"Model '{raw}' was not found in the registry.",
                parameterName);

        return new ResolvedModel(raw, manifest.ModelId, manifest);
    }
}
