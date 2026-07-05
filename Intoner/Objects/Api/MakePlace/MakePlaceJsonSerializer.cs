using Intoner.Objects.Utils;
using System.Text.Json;

namespace Intoner.Objects.Api;

internal static class MakePlaceJsonSerializer
{
    public static readonly JsonSerializerOptions JsonOptions = ObjectJsonSerializerOptionsUtility.CreateLenientIndented();

    public static string Serialize(MakePlaceLayoutDocument document)
        => JsonSerializer.Serialize(document, JsonOptions);

    public static bool TryDeserializeLayout(
        JsonElement root,
        out MakePlaceLayoutDocument document,
        out string errorMessage)
    {
        try
        {
            MakePlaceLayoutDocument? deserialized = root.Deserialize<MakePlaceLayoutDocument>(JsonOptions);
            if (deserialized is not null)
            {
                document = deserialized;
                errorMessage = string.Empty;
                return true;
            }
        }
        catch (JsonException)
        {
            document = null!;
            errorMessage = "The selected MakePlace layout file contains invalid MakePlace data.";
            return false;
        }

        document = null!;
        errorMessage = "The selected MakePlace layout file is empty or invalid.";
        return false;
    }
}
