using System.Text.Json;
using System.Text.Json.Serialization;

namespace Intoner.Objects.Api;

internal sealed record ObjectMakePlaceTransformDocument
{
    [JsonPropertyName("location")]
    public List<float> Location { get; init; } = [0f, 0f, 0f];

    [JsonPropertyName("rotation")]
    public List<float> Rotation { get; init; } = [0f, 0f, 0f, 1f];

    [JsonPropertyName("scale")]
    public List<float> Scale { get; init; } = [1f, 1f, 1f];
}

internal sealed record ObjectMakePlaceFurnitureDocument
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("itemId")]
    public uint ItemId { get; init; }

    [JsonPropertyName("transform")]
    public ObjectMakePlaceTransformDocument Transform { get; init; } = new();

    [JsonPropertyName("properties")]
    public Dictionary<string, JsonElement> Properties { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("attachments")]
    public List<ObjectMakePlaceFurnitureDocument> Attachments { get; init; } = [];
}

internal sealed record ObjectMakePlaceLayoutDocument
{
    [JsonPropertyName("houseSize")]
    public string HouseSize { get; init; } = string.Empty;

    [JsonPropertyName("interiorScale")]
    public float InteriorScale { get; init; } = 1f;

    [JsonPropertyName("interiorFurniture")]
    public List<ObjectMakePlaceFurnitureDocument> InteriorFurniture { get; init; } = [];

    [JsonPropertyName("exteriorScale")]
    public float ExteriorScale { get; init; } = 1f;

    [JsonPropertyName("exteriorFurniture")]
    public List<ObjectMakePlaceFurnitureDocument> ExteriorFurniture { get; init; } = [];
}

