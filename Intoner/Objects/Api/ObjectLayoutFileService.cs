using Intoner.Objects.Filesystem.Configuration;
using Intoner.Objects.Filesystem.Storage;
using Intoner.Objects.Models;
using Intoner.Objects.Runtime;
using Intoner.Objects.Utils;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Intoner.Objects.Api;

/// <summary> imports and exports saved object layouts as json files </summary>
internal interface IObjectLayoutFileService
{
    /// <summary> writes one saved layout to a json file </summary>
    /// <param name="layout">The saved layout snapshot to export.</param>
    /// <param name="path">The destination file path.</param>
    /// <returns>The export result.</returns>
    ObjectLayoutFileExportResult ExportLayout(ObjectLayoutSnapshot? layout, string? path);

    /// <summary> imports one supported json layout file into local layout storage </summary>
    /// <param name="path">The source file path.</param>
    /// <param name="importKind">Optional explicit import format to require.</param>
    /// <returns>The import result.</returns>
    ObjectLayoutFileImportResult ImportLayout(string? path, ObjectLayoutFileImportKind? importKind = null);
}

internal readonly record struct ObjectLayoutFileExportResult(bool Success, string Message);

internal readonly record struct ObjectLayoutFileImportResult(bool Success, ObjectLayoutSnapshot? Layout, string Message);

internal enum ObjectLayoutFileImportKind
{
    ObjectLayout,
    MakePlaceLayout,
}

internal sealed record ObjectLayoutImportPayload(
    string Name,
    IReadOnlyList<ObjectSnapshot> Snapshots,
    IReadOnlyList<string> Folders,
    IReadOnlyDictionary<string, string> FolderColors,
    string SuccessMessage);

internal sealed class ObjectLayoutFileService(
    ILogger<ObjectLayoutFileService> logger,
    IObjectLayoutManager layoutManager,
    IObjectRuntimeLocationService locationService,
    IObjectFileSystem fileSystem,
    IObjectHousingModePolicy housingModePolicy,
    ObjectMakePlaceImportMapper makePlaceImportMapper) : IObjectLayoutFileService
{
    private readonly record struct ObjectLayoutImportSource(JsonElement Root, string Path);

    private delegate bool ImportPayloadParser(ObjectLayoutImportSource source, out ObjectLayoutImportPayload payload, out string errorMessage);

    public ObjectLayoutFileExportResult ExportLayout(ObjectLayoutSnapshot? layout, string? path)
    {
        if (layout is null)
        {
            return ExportFailure("No layout is selected to export.");
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return ExportFailure("No export file path was selected.");
        }

        try
        {
            fileSystem.WriteAllTextAtomic(path, ObjectLayoutJsonSerializer.SerializeLayout(layout));
            return ExportSuccess();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to export object layout {LayoutId} to {Path}", layout.Id, path);
            return ExportFailure("Failed to export the layout file.");
        }
    }

    public ObjectLayoutFileImportResult ImportLayout(string? path, ObjectLayoutFileImportKind? importKind = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return ImportFailure("No layout file was selected.");
        }

        try
        {
            if (!fileSystem.FileExists(path))
            {
                return ImportFailure("The selected layout file no longer exists.");
            }

            if (!TryBuildImportPayload(path, importKind, out var payload, out var errorMessage))
            {
                return ImportFailure(errorMessage);
            }

            if (!housingModePolicy.TryValidateLayout(payload.Snapshots, out var housingModeError))
            {
                return ImportFailure(housingModeError);
            }

            return CreateImportedLayout(payload);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse object layout file {Path}", path);
            return ImportFailure("The selected layout file is not valid json for a supported layout format.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to import object layout file {Path}", path);
            return ImportFailure("Failed to import the selected layout file.");
        }
    }

    private bool TryBuildImportPayload(
        string path,
        ObjectLayoutFileImportKind? importKind,
        out ObjectLayoutImportPayload payload,
        out string errorMessage)
    {
        payload = null!;

        string json = fileSystem.ReadAllText(path);
        using var rootDocument = JsonDocument.Parse(json);
        var root = rootDocument.RootElement;
        if (!TryResolveImportParser(root, importKind, out var parser, out errorMessage))
        {
            return false;
        }

        return parser(new ObjectLayoutImportSource(root, path), out payload, out errorMessage);
    }

    private bool TryParseObjectLayoutImportPayload(ObjectLayoutImportSource source, out ObjectLayoutImportPayload payload, out string errorMessage)
    {
        payload = null!;

        if (!ObjectLayoutJsonSerializer.TryDeserializeLayout(source.Root, out ObjectLayoutSnapshot layout, out errorMessage))
        {
            return false;
        }

        payload = new ObjectLayoutImportPayload(
            layout.Name,
            layout.Objects.Select(static snapshot => snapshot with { LayoutId = null }).ToList(),
            layout.Folders,
            layout.FolderColors,
            string.Empty);
        errorMessage = string.Empty;
        return true;
    }

    private bool TryParseMakePlaceImportPayload(ObjectLayoutImportSource source, out ObjectLayoutImportPayload payload, out string errorMessage)
    {
        payload = null!;

        if (!TryDeserializeDocument(source.Root, "The selected MakePlace layout file is empty or invalid.", out ObjectMakePlaceLayoutDocument? document, out errorMessage))
        {
            return false;
        }

        ObjectRuntimeLocationContext currentLocation = locationService.GetCurrentContext();
        if (currentLocation.Housing.CurrentArea != ObjectHousingArea.Indoor
            || currentLocation.Housing.CurrentSize is not { } currentSize)
        {
            errorMessage = "MakePlace furniture import currently requires standing in an indoor housing territory.";
            return false;
        }

        return makePlaceImportMapper.TryBuildImportPayload(
            document!,
            source.Path,
            currentLocation.CreationContext,
            currentSize.ToString(),
            out payload,
            out errorMessage);
    }

    private ObjectLayoutFileImportResult CreateImportedLayout(ObjectLayoutImportPayload payload)
    {
        var layout = layoutManager.CreateLayout(
            payload.Name,
            payload.Snapshots,
            payload.Folders,
            payload.FolderColors);
        return ImportSuccess(layout, payload.SuccessMessage);
    }

    private bool TryResolveImportParser(
        JsonElement root,
        ObjectLayoutFileImportKind? importKind,
        out ImportPayloadParser parser,
        out string errorMessage)
    {
        var looksLikeObjectLayout = ObjectLayoutFileUtility.LooksLikeObjectLayout(root);
        var looksLikeMakePlaceLayout = ObjectLayoutFileUtility.LooksLikeMakePlaceLayout(root);

        if (importKind == ObjectLayoutFileImportKind.ObjectLayout)
        {
            if (!looksLikeObjectLayout)
            {
                parser = null!;
                errorMessage = "The selected file is not a supported object layout json file.";
                return false;
            }

            parser = TryParseObjectLayoutImportPayload;
            errorMessage = string.Empty;
            return true;
        }

        if (importKind == ObjectLayoutFileImportKind.MakePlaceLayout)
        {
            if (!looksLikeMakePlaceLayout)
            {
                parser = null!;
                errorMessage = "The selected file is not a supported MakePlace layout json file.";
                return false;
            }

            parser = TryParseMakePlaceImportPayload;
            errorMessage = string.Empty;
            return true;
        }

        if (looksLikeObjectLayout)
        {
            parser = TryParseObjectLayoutImportPayload;
            errorMessage = string.Empty;
            return true;
        }

        if (looksLikeMakePlaceLayout)
        {
            parser = TryParseMakePlaceImportPayload;
            errorMessage = string.Empty;
            return true;
        }

        parser = null!;
        errorMessage = "The selected json file is not a supported object layout or MakePlace layout format.";
        return false;
    }

    private static bool TryDeserializeDocument<TDocument>(
        JsonElement root,
        string invalidMessage,
        out TDocument? document,
        out string errorMessage)
        where TDocument : class
    {
        document = root.Deserialize<TDocument>(ObjectLayoutJsonSerializer.JsonOptions);
        if (document is not null)
        {
            errorMessage = string.Empty;
            return true;
        }

        errorMessage = invalidMessage;
        return false;
    }

    private static ObjectLayoutFileExportResult ExportSuccess()
        => new(true, string.Empty);

    private static ObjectLayoutFileExportResult ExportFailure(string message)
        => new(false, message);

    private static ObjectLayoutFileImportResult ImportSuccess(ObjectLayoutSnapshot layout, string message)
        => new(true, layout, message);

    private static ObjectLayoutFileImportResult ImportFailure(string message)
        => new(false, null, message);
}

