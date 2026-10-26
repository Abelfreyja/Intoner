using Intoner.Scene;
using MessagePack;
using MessagePack.Formatters;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Intoner.Objects.Api;

// currently v0.2.5.0 and below location data, converted to the new format as of v0.3.0.0 and any of it's pre testing releases :)
[MessagePackObject(keyAsPropertyName: true, AllowPrivate = true)]
internal sealed record LegacyObjectLocation(
    ushort WorldId, string WorldName, uint TerritoryId, string TerritoryName,
    uint DivisionId, uint WardId, uint HouseId, uint RoomId)
{
    public static JsonSerializerOptions CreateJsonOptions(JsonSerializerOptions current)
        => new(current)
        {
            Converters = { new LegacyObjectLocationConverter(), new LegacySceneLocationConverter() },
        };

    public ObjectLocationData ToLocation()
        => new(WorldId == 0 ? null : new(false, worldIds: [WorldId]),
            TerritoryId, TerritoryName, DivisionId, WardId, HouseId, RoomId);
}

internal sealed class LegacyObjectLocationFormatter : IMessagePackFormatter<ObjectLocationData?>
{
    public ObjectLocationData? Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
        => reader.TryReadNil() ? null : MessagePackSerializer.Deserialize<LegacyObjectLocation>(ref reader, options).ToLocation();

    public void Serialize(ref MessagePackWriter writer, ObjectLocationData? value, MessagePackSerializerOptions options)
        => throw new NotSupportedException("Released locations are read only.");
}

internal sealed class LegacyObjectLocationConverter : JsonConverter<ObjectLocationData>
{
    public override ObjectLocationData Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => (JsonSerializer.Deserialize<LegacyObjectLocation>(ref reader, options)
            ?? throw new JsonException("Missing released location.")).ToLocation();

    public override void Write(Utf8JsonWriter writer, ObjectLocationData value, JsonSerializerOptions options)
        => throw new NotSupportedException("Released locations are read only.");
}

internal sealed class LegacySceneLocationConverter : JsonConverter<SceneCreationContext>
{
    public override SceneCreationContext Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        ObjectLocationData location = JsonSerializer.Deserialize<ObjectLocationData>(ref reader, options)
            ?? throw new JsonException("Missing released location.");
        return ObjectApiMapper.ToCreationContext(location);
    }

    public override void Write(Utf8JsonWriter writer, SceneCreationContext value, JsonSerializerOptions options)
        => throw new NotSupportedException("Released locations are read only.");
}
