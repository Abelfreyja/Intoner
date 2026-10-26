using Intoner.Objects.Api;
using Intoner.Scene;
using Intoner.Services.Serialization;
using Intoner.Services.Storage;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Intoner.Displays;

/// <summary> reads and writes the display section of the scene store </summary>
internal sealed class DisplayPersistence
{
    private const string StoreSection = "displays";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerOptionsUtility.CreateStrictIndented(JsonNamingPolicy.CamelCase))
    {
        IncludeFields = true,
    };
    private static readonly JsonSerializerOptions LegacyJsonOptions = LegacySceneLocationConverter.CreateJsonOptions(JsonOptions);

    private readonly ILogger<DisplayPersistence> _logger;
    private readonly ISceneStore _sceneStore;
    private bool _canWrite = true;

    public DisplayPersistence(ILogger<DisplayPersistence> logger, ISceneStore sceneStore)
    {
        _logger = logger;
        _sceneStore = sceneStore;
    }

    public bool CanWrite => _canWrite && !_sceneStore.IsReadOnly;

    public void BlockWrites() => _canWrite = false;

    public IReadOnlyList<DisplaySnapshot> Load()
    {
        if (!_sceneStore.TryRead(StoreSection, out JsonElement root))
        {
            return [];
        }

        _canWrite = false;
        try
        {
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetPropertyIgnoreCase("formatVersion", out JsonElement property)
                || property.ValueKind != JsonValueKind.Number || !property.TryGetInt32(out int version))
            {
                throw new JsonException("Missing display format version.");
            }

            if (version is 1 or DisplayDocument.CurrentFormatVersion)
            {
                DisplayDocument? document = root.Deserialize<DisplayDocument>(version == 1 ? LegacyJsonOptions : JsonOptions);
                if (document?.Displays is null || document.Displays.Any(static display => display is null))
                {
                    throw new JsonException("Invalid display list.");
                }

                if (!SceneIdentityValidation.TryValidate([document.Displays.Select(static display => display.Id)], out Guid conflictingId))
                {
                    throw new JsonException($"Invalid or duplicate display id {conflictingId:D}.");
                }

                _canWrite = true;
                return document.Displays;
            }

            _logger.LogWarning("display scene data has unsupported format version {FormatVersion}", version);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            _logger.LogWarning(ex, "failed to read display scene data");
        }

        return [];
    }

    public bool TrySave(IReadOnlyList<DisplaySnapshot> snapshots)
        => CanWrite && _sceneStore.TryWrite(
            StoreSection,
            new DisplayDocument
            {
                Displays = snapshots,
            });
}
