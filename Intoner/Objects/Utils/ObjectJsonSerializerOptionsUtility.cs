using System.Text.Json;
using System.Text.Json.Serialization;

namespace Intoner.Objects.Utils;

internal static class ObjectJsonSerializerOptionsUtility
{
    public static JsonSerializerOptions CreateStrictIndented(JsonNamingPolicy? propertyNamingPolicy = null)
        => new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = propertyNamingPolicy,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            Converters =
            {
                new JsonStringEnumConverter(allowIntegerValues: false),
            },
        };
}

