using Dalamud.Plugin.Services;
using Intoner.Objects.Models;
using Intoner.Objects.Runtime;
using Intoner.Services.Storage;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Intoner.Objects.Api;

#pragma warning disable MA0048 // layout file service boundary types stay colocated

/// <summary> transfers layout files in the background and applies imports on the framework thread </summary>
internal interface IObjectLayoutFileService
{
    /// <summary> converts and writes a saved layout without blocking the calling thread </summary>
    /// <param name="layout">the saved layout snapshot to export</param>
    /// <param name="path">the destination file path</param>
    /// <param name="fileKind">the export format</param>
    /// <param name="cancellationToken">cancels pending work before the atomic write begins</param>
    /// <returns>the export result, without an imported layout</returns>
    Task<ObjectLayoutTransferResult> ExportLayoutAsync(ObjectLayoutSnapshot? layout, string? path, ObjectLayoutFileKind fileKind, CancellationToken cancellationToken);

    /// <summary> reads and converts a layout in the background, then validates and saves it on the framework thread </summary>
    /// <param name="path">the source file path</param>
    /// <param name="fileKind">the required format, or null to detect a supported json format</param>
    /// <param name="cancellationToken">cancels pending work before the import is applied</param>
    /// <returns>the import result and saved layout when successful</returns>
    Task<ObjectLayoutTransferResult> ImportLayoutAsync(string? path, ObjectLayoutFileKind? fileKind, CancellationToken cancellationToken);
}

internal sealed class ObjectLayoutFileService(
    ILogger<ObjectLayoutFileService> logger,
    IFramework framework,
    IObjectRuntimeLocationService locationService,
    IPluginFileSystem fileSystem,
    ObjectLayoutTransferCodec codec,
    ObjectLayoutImportService importService) : IObjectLayoutFileService
{
    public async Task<ObjectLayoutTransferResult> ExportLayoutAsync(
        ObjectLayoutSnapshot? layout,
        string? path,
        ObjectLayoutFileKind fileKind,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (layout is null)
        {
            return ObjectLayoutTransferResult.Failure("No layout is selected to export.");
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return ObjectLayoutTransferResult.Failure("No export file path was selected.");
        }

        try
        {
            ObjectRuntimeLocationContext location = fileKind == ObjectLayoutFileKind.MakePlaceLayout
                ? await RunOnFrameworkAsync(locationService.GetCurrentContext, cancellationToken).ConfigureAwait(false)
                : default;
            return await Task.Run(() =>
            {
                if (!codec.TryEncode(layout, fileKind, location, out string json, out string successMessage, out string errorMessage))
                {
                    return ObjectLayoutTransferResult.Failure(errorMessage);
                }

                cancellationToken.ThrowIfCancellationRequested();
                fileSystem.WriteAllTextAtomic(path, json);
                return new ObjectLayoutTransferResult(true, null, successMessage);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to export object layout {LayoutId} to {Path}", layout.Id, path);
            return ObjectLayoutTransferResult.Failure("Failed to export the layout file.");
        }
    }

    public async Task<ObjectLayoutTransferResult> ImportLayoutAsync(
        string? path,
        ObjectLayoutFileKind? fileKind,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(path))
        {
            return ObjectLayoutTransferResult.Failure("No layout file was selected.");
        }

        try
        {
            ObjectRuntimeLocationContext location = fileKind != ObjectLayoutFileKind.ObjectLayout
                ? await RunOnFrameworkAsync(locationService.GetCurrentContext, cancellationToken).ConfigureAwait(false)
                : default;
            (ObjectLayoutImportPayload? payload, string error) = await Task.Run(() =>
            {
                if (!fileSystem.FileExists(path))
                {
                    return ((ObjectLayoutImportPayload?)null, "The selected layout file no longer exists.");
                }

                string json = fileSystem.ReadAllText(path);
                cancellationToken.ThrowIfCancellationRequested();
                return codec.TryDecode(json, path, fileKind, location, out ObjectLayoutImportPayload decoded, out string errorMessage)
                    ? (decoded, string.Empty)
                    : ((ObjectLayoutImportPayload?)null, errorMessage);
            }, cancellationToken).ConfigureAwait(false);

            return payload is null
                ? ObjectLayoutTransferResult.Failure(error)
                : await RunOnFrameworkAsync(() => importService.Apply(payload), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse object layout file {Path}", path);
            return ObjectLayoutTransferResult.Failure("The selected layout file is not valid json for a supported layout format.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to import object layout file {Path}", path);
            return ObjectLayoutTransferResult.Failure("Failed to import the selected layout file.");
        }
    }

    private Task<T> RunOnFrameworkAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return framework.IsInFrameworkUpdateThread
            ? Task.FromResult(action())
            : framework.RunOnTick(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return action();
            }, cancellationToken: cancellationToken);
    }
}

#pragma warning restore MA0048
