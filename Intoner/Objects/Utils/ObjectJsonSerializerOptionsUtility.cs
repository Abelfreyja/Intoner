using System.Text.Json;
using System.Text.Json.Serialization;

namespace Intoner.Objects.Utils;

internal static class ObjectJsonSerializerOptionsUtility
{
    public static JsonSerializerOptions CreateStrictIndented(JsonNamingPolicy? propertyNamingPolicy = null)
        => CreateIndented(propertyNamingPolicy, JsonUnmappedMemberHandling.Disallow);

    public static JsonSerializerOptions CreateLenientIndented(JsonNamingPolicy? propertyNamingPolicy = null)
        => CreateIndented(propertyNamingPolicy, null);

    private static JsonSerializerOptions CreateIndented(
        JsonNamingPolicy? propertyNamingPolicy,
        JsonUnmappedMemberHandling? unmappedMemberHandling)
    {
        JsonSerializerOptions options = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = propertyNamingPolicy,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
            Converters =
            {
                new JsonStringEnumConverter(allowIntegerValues: false),
            },
        };

        if (unmappedMemberHandling.HasValue)
        {
            options.UnmappedMemberHandling = unmappedMemberHandling.Value;
        }

        return options;
    }
}

