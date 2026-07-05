using Intoner.Objects.Filesystem.Storage;
using Intoner.Objects.Models;
using Intoner.Objects.Runtime;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Intoner.Objects.Api;

#pragma warning disable MA0048 // layout file service boundary types stay colocated

/// <summary> imports and exports saved object layouts as json files </summary>
internal interface IObjectLayoutFileService
{
    /// <summary> writes one saved layout to a json file </summary>
    /// <param name="layout">The saved layout snapshot to export.</param>
    /// <param name="path">The destination file path.</param>
    /// <param name="fileKind">The export format.</param>
    /// <returns>The export result.</returns>
    ObjectLayoutFileExportResult ExportLayout(ObjectLayoutSnapshot? layout, string? path, ObjectLayoutFileKind fileKind = ObjectLayoutFileKind.ObjectLayout);

    /// <summary> imports one supported json layout file into local layout storage </summary>
    /// <param name="path">The source file path.</param>
    /// <param name="fileKind">Optional explicit import format to require.</param>
    /// <returns>The import result.</returns>
    ObjectLayoutFileImportResult ImportLayout(string? path, ObjectLayoutFileKind? fileKind = null);
}

internal readonly record struct ObjectLayoutFileExportResult(bool Success, string Message);

internal readonly record struct ObjectLayoutFileImportResult(bool Success, ObjectLayoutSnapshot? Layout, string Message);

internal enum ObjectLayoutFileKind
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
    MakePlaceImportMapper makePlaceImportMapper,
    MakePlaceExportMapper makePlaceExportMapper) : IObjectLayoutFileService
{
    public ObjectLayoutFileExportResult ExportLayout(ObjectLayoutSnapshot? layout, string? path, ObjectLayoutFileKind fileKind = ObjectLayoutFileKind.ObjectLayout)
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
            if (!TryBuildExportJson(layout, fileKind, out string json, out string successMessage, out string errorMessage))
            {
                return ExportFailure(errorMessage);
            }

            fileSystem.WriteAllTextAtomic(path, json);
            return ExportSuccess(successMessage);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to export object layout {LayoutId} to {Path}", layout.Id, path);
            return ExportFailure("Failed to export the layout file.");
        }
    }

    public ObjectLayoutFileImportResult ImportLayout(string? path, ObjectLayoutFileKind? fileKind = null)
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

            if (!TryBuildImportPayload(path, fileKind, out ObjectLayoutImportPayload payload, out string errorMessage))
            {
                return ImportFailure(errorMessage);
            }

            if (!housingModePolicy.TryValidateLayout(payload.Snapshots, out string housingModeError))
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
        ObjectLayoutFileKind? fileKind,
        out ObjectLayoutImportPayload payload,
        out string errorMessage)
    {
        payload = null!;

        string json = fileSystem.ReadAllText(path);
        using var rootDocument = JsonDocument.Parse(json);
        JsonElement root = rootDocument.RootElement;
        if (!TryResolveImportKind(root, fileKind, out ObjectLayoutFileKind resolvedKind, out errorMessage))
        {
            return false;
        }

        return resolvedKind == ObjectLayoutFileKind.ObjectLayout
            ? TryParseObjectLayoutImportPayload(root, out payload, out errorMessage)
            : TryParseMakePlaceImportPayload(root, path, out payload, out errorMessage);
    }

    private bool TryParseObjectLayoutImportPayload(JsonElement root, out ObjectLayoutImportPayload payload, out string errorMessage)
    {
        payload = null!;

        if (!ObjectLayoutJsonSerializer.TryDeserializeLayout(root, out ObjectLayoutSnapshot layout, out errorMessage))
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

    private bool TryParseMakePlaceImportPayload(JsonElement root, string sourcePath, out ObjectLayoutImportPayload payload, out string errorMessage)
    {
        payload = null!;

        if (!MakePlaceJsonSerializer.TryDeserializeLayout(root, out MakePlaceLayoutDocument document, out errorMessage))
        {
            return false;
        }

        ObjectRuntimeLocationContext currentLocation = locationService.GetCurrentContext();

        return makePlaceImportMapper.TryBuildImportPayload(
            document,
            sourcePath,
            currentLocation,
            out payload,
            out errorMessage);
    }

    private bool TryBuildExportJson(
        ObjectLayoutSnapshot layout,
        ObjectLayoutFileKind fileKind,
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
            return true;
        }

        ObjectRuntimeLocationContext currentLocation = locationService.GetCurrentContext();
        if (!makePlaceExportMapper.TryBuildExportDocument(layout, currentLocation, out MakePlaceLayoutDocument document, out successMessage, out errorMessage))
        {
            return false;
        }

        json = MakePlaceJsonSerializer.Serialize(document);
        return true;
    }

    private ObjectLayoutFileImportResult CreateImportedLayout(ObjectLayoutImportPayload payload)
    {
        ObjectLayoutSnapshot layout = layoutManager.CreateLayout(
            payload.Name,
            payload.Snapshots,
            payload.Folders,
            payload.FolderColors);
        return ImportSuccess(layout, payload.SuccessMessage);
    }

    private static bool TryResolveImportKind(
        JsonElement root,
        ObjectLayoutFileKind? fileKind,
        out ObjectLayoutFileKind resolvedKind,
        out string errorMessage)
    {
        bool looksLikeObjectLayout = ObjectLayoutFileUtility.LooksLikeObjectLayout(root);
        bool looksLikeMakePlaceLayout = ObjectLayoutFileUtility.LooksLikeMakePlaceLayout(root);

        if (fileKind == ObjectLayoutFileKind.ObjectLayout)
        {
            if (!looksLikeObjectLayout)
            {
                resolvedKind = default;
                errorMessage = "The selected file is not a supported object layout json file.";
                return false;
            }

            resolvedKind = ObjectLayoutFileKind.ObjectLayout;
            errorMessage = string.Empty;
            return true;
        }

        if (fileKind == ObjectLayoutFileKind.MakePlaceLayout)
        {
            if (!looksLikeMakePlaceLayout)
            {
                resolvedKind = default;
                errorMessage = "The selected file is not a supported MakePlace layout json file.";
                return false;
            }

            resolvedKind = ObjectLayoutFileKind.MakePlaceLayout;
            errorMessage = string.Empty;
            return true;
        }

        if (looksLikeObjectLayout)
        {
            resolvedKind = ObjectLayoutFileKind.ObjectLayout;
            errorMessage = string.Empty;
            return true;
        }

        if (looksLikeMakePlaceLayout)
        {
            resolvedKind = ObjectLayoutFileKind.MakePlaceLayout;
            errorMessage = string.Empty;
            return true;
        }

        resolvedKind = default;
        errorMessage = "The selected json file is not a supported object layout or MakePlace layout format.";
        return false;
    }

    private static ObjectLayoutFileExportResult ExportSuccess(string message)
        => new(true, message);

    private static ObjectLayoutFileExportResult ExportFailure(string message)
        => new(false, message);

    private static ObjectLayoutFileImportResult ImportSuccess(ObjectLayoutSnapshot layout, string message)
        => new(true, layout, message);

    private static ObjectLayoutFileImportResult ImportFailure(string message)
        => new(false, null, message);
}

#pragma warning restore MA0048
