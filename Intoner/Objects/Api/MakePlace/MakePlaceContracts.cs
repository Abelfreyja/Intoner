using System.Text.Json;
using System.Text.Json.Serialization;

namespace Intoner.Objects.Api;

#pragma warning disable MA0048 // makeplace json schema documents stay colocated

internal sealed record MakePlaceTransformDocument
{
    [JsonPropertyName("location")]
    public List<float> Location { get; init; } = [0f, 0f, 0f];

    [JsonPropertyName("rotation")]
    public List<float> Rotation { get; init; } = [0f, 0f, 0f, 1f];

    [JsonPropertyName("scale")]
    public List<float> Scale { get; init; } = [1f, 1f, 1f];
}

internal record MakePlaceBasicItemDocument
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("itemId")]
    public uint ItemId { get; init; }
}

internal sealed record MakePlaceFixtureDocument : MakePlaceBasicItemDocument
{
    [JsonPropertyName("level")]
    public string Level { get; init; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;
}

internal sealed record MakePlaceFurnitureDocument
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("itemId")]
    public uint ItemId { get; init; }

    [JsonPropertyName("transform")]
    public MakePlaceTransformDocument Transform { get; init; } = new();

    [JsonPropertyName("properties")]
    public Dictionary<string, JsonElement> Properties { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("attachments")]
    public List<MakePlaceFurnitureDocument> Attachments { get; init; } = [];
}

internal sealed record MakePlaceLayoutDocument
{
    [JsonPropertyName("playerTransform")]
    public MakePlaceTransformDocument PlayerTransform { get; init; } = new();

    [JsonPropertyName("houseSize")]
    public string HouseSize { get; init; } = string.Empty;

    [JsonPropertyName("interiorScale")]
    public float InteriorScale { get; init; } = 1f;

    [JsonPropertyName("interiorFixture")]
    public List<MakePlaceFixtureDocument> InteriorFixture { get; init; } = [];

    [JsonPropertyName("interiorFurniture")]
    public List<MakePlaceFurnitureDocument> InteriorFurniture { get; init; } = [];

    [JsonPropertyName("exteriorScale")]
    public float ExteriorScale { get; init; } = 1f;

    [JsonPropertyName("exteriorFixture")]
    public List<MakePlaceFixtureDocument> ExteriorFixture { get; init; } = [];

    [JsonPropertyName("exteriorFurniture")]
    public List<MakePlaceFurnitureDocument> ExteriorFurniture { get; init; } = [];

    [JsonPropertyName("properties")]
    public Dictionary<string, JsonElement> Properties { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

#pragma warning restore MA0048
