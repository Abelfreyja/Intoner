using Intoner.Objects.Assets;
using Intoner.Objects.Models;
using Intoner.Objects.Utils;

namespace Intoner.Objects.Resources;

/// <summary> object root path kind </summary>
internal enum ObjectRootPathKind
{
    BgModel,
    FurnitureSharedGroup,
    Vfx,
}

/// <summary> resolved object root path status </summary>
internal enum ObjectResolvedRootPathStatus
{
    Ready,
    ResourceHooksUnavailable,
    InvalidRedirectKind,
    UnsupportedLocalFile,
    UnsupportedMemoryResource,
}

/// <summary> resolved object root resource request </summary>
internal readonly record struct ObjectResolvedRootPath
{
    internal ObjectResolvedRootPath(
        string requestedPath,
        string createPath,
        string resolvedPath,
        string resourceCollectionId,
        ObjectResolvedPathKind resolvedPathKind,
        ObjectResolvedRootPathStatus status)
    {
        RequestedPath = requestedPath;
        CreatePath = createPath;
        ResolvedPath = resolvedPath;
        ResourceCollectionId = resourceCollectionId;
        ResolvedPathKind = resolvedPathKind;
        Status = status;
    }

    public string RequestedPath { get; } = string.Empty;
    public string CreatePath { get; } = string.Empty;
    public string ResolvedPath { get; } = string.Empty;
    public string ResourceCollectionId { get; } = string.Empty;
    public ObjectResolvedPathKind ResolvedPathKind { get; } = ObjectResolvedPathKind.GamePath;
    public ObjectResolvedRootPathStatus Status { get; } = ObjectResolvedRootPathStatus.Ready;

    public bool Redirected
        => !string.Equals(RequestedPath, ResolvedPath, StringComparison.OrdinalIgnoreCase);

    public bool IsReady
        => Status == ObjectResolvedRootPathStatus.Ready;

    public ObjectResolvedPath ToResolvedPath()
        => ResolvedPathKind switch
        {
            ObjectResolvedPathKind.LocalFile => ObjectResolvedPath.FromLocalFile(ResolvedPath),
            ObjectResolvedPathKind.Memory => ObjectResolvedPath.FromMemory(ResolvedPath),
            _ => ObjectResolvedPath.FromGamePath(ResolvedPath),
        };
}

/// <summary>
/// Resolves root object resource requests against the object owned resource collection set.
/// </summary>
internal interface IObjectPathResolver
{
    /// <summary>
    /// Resolves one root object resource request.
    /// </summary>
    /// <param name="snapshot">the object snapshot requesting the resource</param>
    /// <param name="kind">the root resource kind being created</param>
    /// <param name="requestedPath">the requested game path from the snapshot model</param>
    /// <returns>the resolved root resource request</returns>
    ObjectResolvedRootPath ResolveRootPath(ObjectSnapshot snapshot, ObjectRootPathKind kind, string requestedPath);
}

internal sealed class ObjectPathResolver : IObjectPathResolver
{
    private readonly IObjectResolvedCollectionStore _collectionStore;
    private readonly Func<IObjectFileReadService> _fileReadServiceFactory;
    private readonly Func<IObjectResourceLoader> _resourceLoaderFactory;

    public ObjectPathResolver(
        IObjectResolvedCollectionStore collectionStore,
        Func<IObjectFileReadService> fileReadServiceFactory,
        Func<IObjectResourceLoader> resourceLoaderFactory)
    {
        _collectionStore = collectionStore;
        _fileReadServiceFactory = fileReadServiceFactory;
        _resourceLoaderFactory = resourceLoaderFactory;
    }

    public ObjectResolvedRootPath ResolveRootPath(ObjectSnapshot snapshot, ObjectRootPathKind kind, string requestedPath)
    {
        string normalizedRequestedPath = GameAssetPathRules.NormalizeGamePath(requestedPath);
        string requestedCollectionId = ObjectCollectionKeyUtility.NormalizeCollectionId(snapshot.CollectionId);
        string resourceCollectionId = string.Empty;
        ObjectResolvedPath resolvedPath = ObjectResolvedPath.FromGamePath(normalizedRequestedPath);
        string createPath = normalizedRequestedPath;
        var status = ObjectResolvedRootPathStatus.Ready;
        if (requestedCollectionId.Length > 0
         && _collectionStore.TryGetCollection(requestedCollectionId, out ObjectCollectionResolveData collection)
         && collection.Redirects.Count > 0)
        {
            resourceCollectionId = requestedCollectionId;
            if (!ResourceLoader.CanResolveCollectionResources(kind))
            {
                status = ObjectResolvedRootPathStatus.ResourceHooksUnavailable;
            }
            else if (collection.TryResolvePath(normalizedRequestedPath, out ObjectResolvedPath redirectedPath))
            {
                resolvedPath = redirectedPath;
                if (!CanUseRootPath(kind, normalizedRequestedPath, redirectedPath))
                {
                    status = ObjectResolvedRootPathStatus.InvalidRedirectKind;
                }
                else if (redirectedPath.Kind == ObjectResolvedPathKind.GamePath)
                {
                    createPath = redirectedPath.Path;
                }
            }
        }

        if (status == ObjectResolvedRootPathStatus.Ready
            && resolvedPath.IsLocalFile
            && !FileReadService.CanLoadLocalFilePath(resolvedPath.Path))
        {
            status = ObjectResolvedRootPathStatus.UnsupportedLocalFile;
        }
        else if (status == ObjectResolvedRootPathStatus.Ready
            && resolvedPath.IsMemory
            && !FileReadService.CanLoadMemoryResourcePath(resolvedPath.Path))
        {
            status = ObjectResolvedRootPathStatus.UnsupportedMemoryResource;
        }

        return new ObjectResolvedRootPath(
            normalizedRequestedPath,
            createPath,
            resolvedPath.Path,
            resourceCollectionId,
            resolvedPath.Kind,
            status);
    }

    private static bool CanUseRootPath(ObjectRootPathKind kind, string requestedPath, ObjectResolvedPath resolvedPath)
    {
        string resourcePath = resolvedPath.Kind == ObjectResolvedPathKind.GamePath
            ? resolvedPath.ResourceGamePath
            : requestedPath;
        return kind switch
        {
            ObjectRootPathKind.BgModel => ObjectAssetPathRules.IsCatalogModelPath(resourcePath),
            ObjectRootPathKind.FurnitureSharedGroup => ObjectAssetPathRules.IsCatalogSharedGroupPath(resourcePath),
            ObjectRootPathKind.Vfx => GameAssetPathRules.IsFileKind(resourcePath, GameAssetFileKind.Avfx),
            _ => false,
        };
    }

    private IObjectFileReadService FileReadService
        => _fileReadServiceFactory();

    private IObjectResourceLoader ResourceLoader
        => _resourceLoaderFactory();
}


