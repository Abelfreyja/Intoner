using Intoner.Scene;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Intoner.Objects.Api;

internal sealed class LegacySceneLocationConverter : JsonConverter<SceneCreationContext>
{
    public static JsonSerializerOptions CreateJsonOptions(JsonSerializerOptions current)
        => new(current)
        {
            Converters = { new LegacyObjectLocationConverter(), new LegacySceneLocationConverter() },
        };

    public override SceneCreationContext Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        ObjectLocationData location = JsonSerializer.Deserialize<ObjectLocationData>(ref reader, options)
            ?? throw new JsonException("Missing legacy location data.");
        return ObjectApiMapper.ToCreationContext(location);
    }

    public override void Write(Utf8JsonWriter writer, SceneCreationContext value, JsonSerializerOptions options)
        => throw new NotSupportedException("Writing the legacy location format is not supported.");
}
