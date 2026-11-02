using System.Text.Json.Serialization;

namespace StockAnalyzer.Core.Models.Training;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ModelArtifactKind
{
    Prediction,
    ReinforcementLearning,
    PortfolioReinforcementLearning
}

public readonly record struct ModelMetadataKey(
    [property: JsonPropertyName("kind")] ModelArtifactKind Kind,
    [property: JsonPropertyName("modelId")] string ModelId)
{
    public override string ToString() => $"{Kind}:{ModelId}";
}
