using Intoner.Objects.Assets;
using Penumbra.Api.Enums;

namespace Intoner.Objects.Resources;

/// <summary> object root path kind </summary>
internal enum ObjectRootPathKind
{
    BgModel,
    FurnitureSharedGroup,
    Vfx,
    LightProjection,
}

/// <summary> resolved object root path status </summary>
internal enum ObjectResolvedRootPathStatus
{
    Ready,
    ResourceHooksUnavailable,
    InvalidRedirectKind,
    UnsupportedLocalFile,
    UnsupportedMemoryResource,
    InvalidNativePath,
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

/// <summary> resolves object root paths against their resource collections </summary>
internal sealed class ObjectPathResolver
{
    private readonly ObjectFileReadService _fileReadService;
    private readonly IObjectResourceLoader _resourceLoader;

    public ObjectPathResolver(
        ObjectFileReadService fileReadService,
        IObjectResourceLoader resourceLoader)
    {
        _fileReadService = fileReadService;
        _resourceLoader = resourceLoader;
    }

    public ObjectResolvedRootPath ResolveRootPath(ObjectRootPathKind kind, string requestedPath, ObjectCollectionResolveData? collection)
    {
        ObjectResolvedPath resolvedPath = ObjectResolvedPath.FromGamePath(requestedPath);
        string normalizedRequestedPath = resolvedPath.Path;

        if (normalizedRequestedPath.Length == 0)
        {
            return new ObjectResolvedRootPath(string.Empty, string.Empty, string.Empty, string.Empty,
                ObjectResolvedPathKind.GamePath, ObjectResolvedRootPathStatus.InvalidNativePath);
        }

        string resourceCollectionId = string.Empty;
        var status = ObjectResolvedRootPathStatus.Ready;
        if (collection is { Redirects.Count: > 0 })
        {
            resourceCollectionId = collection.CollectionId;
            if (!_resourceLoader.CanResolveCollectionResources(kind))
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
            }
        }

        long resourceScopeId = resourceCollectionId.Length > 0 ? collection!.ResourceScopeId : 0;
        string createPath = ObjectScopedResourcePathUtility.Create(resourceScopeId, normalizedRequestedPath);

        if (status == ObjectResolvedRootPathStatus.Ready
         && (createPath.Length == 0
          || (!string.Equals(normalizedRequestedPath, resolvedPath.Path, StringComparison.Ordinal)
              && ObjectScopedResourcePathUtility.Create(resourceScopeId, resolvedPath.Path).Length == 0)))
        {
            status = ObjectResolvedRootPathStatus.InvalidNativePath;
        }

        if (status == ObjectResolvedRootPathStatus.Ready
            && resolvedPath.IsLocalFile
            && !_fileReadService.CanLoadLocalFilePath(resolvedPath.Path, ResourceType.FromPath(normalizedRequestedPath)))
        {
            status = ObjectResolvedRootPathStatus.UnsupportedLocalFile;
        }
        else if (status == ObjectResolvedRootPathStatus.Ready
            && resolvedPath.IsMemory
            && !_fileReadService.CanLoadMemoryResourcePath(resolvedPath.Path))
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
            ObjectRootPathKind.LightProjection => GameAssetPathRules.IsFileKind(resourcePath, GameAssetFileKind.Tex),
            _ => false,
        };
    }
}
