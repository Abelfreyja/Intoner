using Intoner.Objects.Models;
using Intoner.Objects.Runtime;
using System.Text.Json;

namespace Intoner.Objects.Api;

/// <summary> converts layout json and prepared imports without file access or editor mutation </summary>
internal sealed class ObjectLayoutTransferCodec(
    MakePlaceImportMapper makePlaceImportMapper,
    MakePlaceExportMapper makePlaceExportMapper)
{
    public bool TryDecode(
        string json,
        string sourceName,
        ObjectLayoutFileKind? fileKind,
        ObjectRuntimeLocationContext location,
        out ObjectLayoutImportPayload payload,
        out string errorMessage)
    {
        payload = null!;
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (!TryResolveImportKind(root, fileKind, out ObjectLayoutFileKind resolvedKind, out errorMessage))
        {
            return false;
        }

        if (resolvedKind == ObjectLayoutFileKind.ObjectLayout)
        {
            if (!ObjectLayoutJsonSerializer.TryDeserializeLayout(root, out ObjectLayoutSnapshot layout, out errorMessage))
            {
                return false;
            }

            payload = new ObjectLayoutImportPayload(
                layout.Name,
                layout.Objects.Select(static snapshot => snapshot with { LayoutId = null }).ToList(),
                layout.Folders,
                $"Imported layout '{layout.Name}' from json.");
            return true;
        }

        if (!MakePlaceJsonSerializer.TryDeserializeLayout(root, out MakePlaceLayoutDocument makePlaceDocument, out errorMessage)
         || !makePlaceImportMapper.TryBuildImportPayload(makePlaceDocument, sourceName, location, out payload, out errorMessage))
        {
            return false;
        }

        payload = payload with { RequiredLocation = location };
        return true;
    }

    public bool TryEncode(
        ObjectLayoutSnapshot layout,
        ObjectLayoutFileKind fileKind,
        ObjectRuntimeLocationContext location,
        out string json,
        out string successMessage,
        out string errorMessage)
    {
        json = string.Empty;
        successMessage = string.Empty;
        errorMessage = string.Empty;
        if (fileKind == ObjectLayoutFileKind.ObjectLayout)
        {
            json = ObjectLayoutJsonSerializer.SerializeLayout(layout);
            successMessage = $"Exported layout '{layout.Name}' to json.";
            return true;
        }

        if (fileKind != ObjectLayoutFileKind.MakePlaceLayout)
        {
            errorMessage = "The selected layout export format is not supported.";
            return false;
        }

        if (!makePlaceExportMapper.TryBuildExportDocument(layout, location, out MakePlaceLayoutDocument document, out successMessage, out errorMessage))
        {
            return false;
        }

        json = MakePlaceJsonSerializer.Serialize(document);
        return true;
    }

    private static bool TryResolveImportKind(
        JsonElement root,
        ObjectLayoutFileKind? fileKind,
        out ObjectLayoutFileKind resolvedKind,
        out string errorMessage)
    {
        bool looksLikeObjectLayout = ObjectLayoutFileUtility.LooksLikeObjectLayout(root);
        bool looksLikeMakePlaceLayout = ObjectLayoutFileUtility.LooksLikeMakePlaceLayout(root);
        resolvedKind = default;
        errorMessage = string.Empty;
        switch (fileKind)
        {
            case ObjectLayoutFileKind.ObjectLayout when !looksLikeObjectLayout:
                errorMessage = "The selected file is not a supported object layout json file.";
                return false;
            case ObjectLayoutFileKind.MakePlaceLayout when !looksLikeMakePlaceLayout:
                errorMessage = "The selected file is not a supported MakePlace layout json file.";
                return false;
            case ObjectLayoutFileKind.ObjectLayout:
            case null when looksLikeObjectLayout:
                resolvedKind = ObjectLayoutFileKind.ObjectLayout;
                return true;
            case ObjectLayoutFileKind.MakePlaceLayout:
            case null when looksLikeMakePlaceLayout:
                resolvedKind = ObjectLayoutFileKind.MakePlaceLayout;
                return true;
            default:
                errorMessage = "The selected json file is not a supported object layout or MakePlace layout format.";
                return false;
        }
    }
}
