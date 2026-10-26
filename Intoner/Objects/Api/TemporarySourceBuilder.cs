using Intoner.Objects.Collections;
using Intoner.Objects.Models;
using Intoner.Objects.Resources;
using Intoner.Objects.Runtime;
using Intoner.Objects.Utils;
using System.Security.Cryptography;
using System.Text;

namespace Intoner.Objects.Api;

/// <summary> builds temporary source and collection payloads from authored objects </summary>
internal interface ITemporarySourceBuilder
{
    /// <summary> builds a complete temporary source payload </summary>
    /// <param name="request"> the source build request </param>
    /// <param name="cancellationToken"> cancellation token </param>
    /// <returns> the source payload and build diagnostics </returns>
    Task<TemporarySourceBuildResult> BuildTemporarySourceAsync(
        TemporarySourceBuildRequest? request,
        CancellationToken cancellationToken);
}

internal sealed class TemporarySourceBuilder : ITemporarySourceBuilder
{
    private sealed record ResolvedCollection(
        ObjectCollectionResolveResult Result,
        IReadOnlyDictionary<string, CollectionResourceView> ResourceViews);

    private readonly IObjectCollectionManager  _collectionManager;
    private readonly IObjectCollectionResolver _collectionResolver;
    private readonly IObjectKindService        _objectKindService;

    public TemporarySourceBuilder(
        IObjectCollectionManager collectionManager,
        IObjectCollectionResolver collectionResolver,
        IObjectKindService objectKindService)
    {
        _collectionManager  = collectionManager;
        _collectionResolver = collectionResolver;
        _objectKindService  = objectKindService;
    }

    public async Task<TemporarySourceBuildResult> BuildTemporarySourceAsync(
        TemporarySourceBuildRequest? request,
        CancellationToken cancellationToken)
    {
        if (request?.Objects is null)
        {
            return CreateInvalidResult("temporary source build request is invalid");
        }

        string sourceId = TemporarySourceUtility.NormalizeKey(request.SourceId);
        if (sourceId.Length == 0 || request.SessionId == Guid.Empty || request.Revision <= 0)
        {
            return CreateInvalidResult("source id, session id, and positive revision are required");
        }

        if (!TryPrepareSnapshots(request.Objects, cancellationToken, out List<ObjectSnapshot> snapshots))
        {
            return CreateInvalidResult("temporary source build contains invalid objects");
        }

        Dictionary<string, ObjectCollection?> collections = CaptureCollections(snapshots, cancellationToken);
        Dictionary<string, ResolvedCollection> resolvedCollections = await ResolveCollectionsAsync(
            snapshots, collections, cancellationToken).ConfigureAwait(false);
        List<TemporarySourceBuildDiagnostic> diagnostics = [];
        Dictionary<string, TemporaryObjectCollection> generatedCollectionsBySignature = new(StringComparer.Ordinal);
        Dictionary<(string Collection, ObjectResourceRoots Roots), string> collectionIdsByView = [];
        SortedSet<string> localFilePaths = new(StringComparer.OrdinalIgnoreCase);

        for (int index = 0; index < snapshots.Count; ++index)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectSnapshot snapshot = snapshots[index];
            string authoredCollectionId = snapshot.CollectionId;

            if (authoredCollectionId.Length == 0)
            {
                continue;
            }

            ObjectCollection? authoredCollection = collections[authoredCollectionId];
            if (authoredCollection is null)
            {
                diagnostics.Add(CreateDiagnostic(
                    snapshot,
                    TemporarySourceBuildDiagnosticSeverity.Error,
                    "assigned object collection was not found"));
                continue;
            }

            ObjectResourceRoots roots = ObjectResourceRoots.FromModel(snapshot.Model);
            string rootPath = roots.Primary;
            if (rootPath.Length == 0)
            {
                diagnostics.Add(CreateDiagnostic(
                    snapshot,
                    TemporarySourceBuildDiagnosticSeverity.Info,
                    "object kind has no resource collection scope"));
                snapshots[index] = snapshot with { CollectionId = string.Empty };
                continue;
            }

            ResolvedCollection resolvedCollection = resolvedCollections[authoredCollectionId];

            if (!ValidateResolution(snapshot, resolvedCollection.Result, diagnostics))
            {
                continue;
            }

            if (resolvedCollection.Result.Redirects.Count == 0)
            {
                snapshots[index] = snapshot with { CollectionId = string.Empty };
                continue;
            }

            if (!resolvedCollection.ResourceViews.TryGetValue(rootPath, out CollectionResourceView? view))
            {
                diagnostics.Add(CreateDiagnostic(snapshot, TemporarySourceBuildDiagnosticSeverity.Error,
                    "resolved collection has no resource view for this object"));
                continue;
            }

            var viewKey = (authoredCollectionId, roots);
            if (!collectionIdsByView.TryGetValue(viewKey, out string? collectionId))
            {
                IReadOnlyList<ObjectPathRedirection> redirects = view.Redirects;
                if (roots.Collision.Length > 0)
                {
                    if (!resolvedCollection.ResourceViews.TryGetValue(roots.Collision, out CollectionResourceView? collisionView))
                    {
                        diagnostics.Add(CreateDiagnostic(snapshot, TemporarySourceBuildDiagnosticSeverity.Error,
                            "resolved collection has no resource view for this object's collider"));
                        continue;
                    }

                    redirects = view.Redirects.Concat(collisionView.Redirects)
                        .DistinctBy(static redirect => redirect.RequestedPath, StringComparer.OrdinalIgnoreCase).ToArray();
                }

                collectionId = GetTemporaryCollectionId(authoredCollection.Name, redirects);
                collectionIdsByView.Add(viewKey, collectionId);
            }

            snapshots[index] = snapshot with { CollectionId = collectionId };
        }

        if (diagnostics.Any(static diagnostic => diagnostic.Severity == TemporarySourceBuildDiagnosticSeverity.Error))
        {
            return new TemporarySourceBuildResult(
                TemporarySourceBuildStatus.ResolutionFailed,
                "temporary source build failed because required collection resources could not be resolved",
                null,
                [],
                diagnostics);
        }

        cancellationToken.ThrowIfCancellationRequested();

        List<WorldObject> payloadObjects = snapshots.Select(ObjectApiMapper.ToWorldObject).ToList();
        List<TemporaryObjectCollection> payloadCollections = generatedCollectionsBySignature.Values
            .OrderBy(static collection => collection.CollectionId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        TemporarySourceBuildStatus status = diagnostics.Any(static diagnostic =>
                diagnostic.Severity == TemporarySourceBuildDiagnosticSeverity.Warning)
            ? TemporarySourceBuildStatus.CompletedWithWarnings
            : TemporarySourceBuildStatus.Success;

        string message = $"built {payloadObjects.Count} objects, {payloadCollections.Count} temporary collections, and {localFilePaths.Count} local files";
        return new TemporarySourceBuildResult(
            status,
            diagnostics.Count == 0 ? message : $"{message} with {diagnostics.Count} diagnostics",
            new TemporarySourceApplyRequest(
                sourceId,
                request.SessionId,
                TextUtility.TrimOrEmpty(request.Name),
                request.Revision,
                payloadObjects,
                payloadCollections),
            localFilePaths.ToList(),
            diagnostics);

        string GetTemporaryCollectionId(string name, IReadOnlyList<ObjectPathRedirection> redirects)
        {
            if (redirects.Count == 0)
            {
                return string.Empty;
            }

            ObjectPathRedirection[] orderedRedirects = redirects
                .OrderBy(static redirect => redirect.RequestedPath, StringComparer.OrdinalIgnoreCase).ToArray();
            string signature = BuildRedirectSignature(orderedRedirects);

            if (generatedCollectionsBySignature.TryGetValue(signature, out TemporaryObjectCollection? existing))
            {
                return existing.CollectionId;
            }

            Span<byte> hash = stackalloc byte[32];
            SHA256.HashData(Encoding.UTF8.GetBytes(signature), hash);
            string collectionId = $"tempcoll_{Convert.ToHexString(hash[..16]).ToLowerInvariant()}";
            generatedCollectionsBySignature.Add(signature, new TemporaryObjectCollection(
                collectionId,
                TextUtility.TrimOrFallback(name, collectionId),
                orderedRedirects.Select(ToTemporaryRedirect).ToArray()));
            localFilePaths.UnionWith(orderedRedirects
                .Where(static redirect => redirect.ResolvedPath.IsLocalFile)
                .Select(static redirect => redirect.ResolvedPath.Path));
            return collectionId;
        }
    }

    private bool TryPrepareSnapshots(
        IReadOnlyList<WorldObject> objects,
        CancellationToken cancellationToken,
        out List<ObjectSnapshot> snapshots)
    {
        snapshots = new List<ObjectSnapshot>(objects.Count);
        HashSet<Guid> objectIds = [];
        foreach (WorldObject? dto in objects)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!ObjectApiMapper.TryToDetachedSnapshot(dto, out ObjectSnapshot snapshot)
                || !objectIds.Add(snapshot.Id)
                || !_objectKindService.TrySanitizeSnapshot(snapshot, out snapshot))
            {
                snapshots = [];
                return false;
            }

            snapshots.Add(snapshot);
        }

        return true;
    }

    private Dictionary<string, ObjectCollection?> CaptureCollections(
        IReadOnlyList<ObjectSnapshot> snapshots,
        CancellationToken cancellationToken)
    {
        Dictionary<string, ObjectCollection?> collections = new(StringComparer.OrdinalIgnoreCase);
        foreach (string collectionId in snapshots.Select(static snapshot => snapshot.CollectionId))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (collectionId.Length > 0 && !collections.ContainsKey(collectionId))
            {
                collections.Add(collectionId,
                    _collectionManager.TryGetCollection(collectionId, out ObjectCollectionSnapshot collection) ? collection.Record : null);
            }
        }

        return collections;
    }

    private async Task<Dictionary<string, ResolvedCollection>> ResolveCollectionsAsync(
        IReadOnlyList<ObjectSnapshot> snapshots,
        IReadOnlyDictionary<string, ObjectCollection?> collections,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Dictionary<string, ResolvedCollection> resolved = new(StringComparer.OrdinalIgnoreCase);
        foreach (IGrouping<string, ObjectSnapshot> usage in snapshots
                     .Where(static snapshot => snapshot.CollectionId.Length > 0
                         && ObjectSnapshotUtility.GetRootResourcePath(snapshot).Length > 0)
                     .GroupBy(static snapshot => snapshot.CollectionId, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectCollection? collection = collections[usage.Key];

            if (collection is null)
            {
                continue;
            }

            ObjectCollectionResolveResult result;
            try
            {
                result = await _collectionResolver.ResolveAsync(collection, usage.ToArray(), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                result = new ObjectCollectionResolveResult
                {
                    IsComplete = false,
                    ResolveState = ObjectCollectionResolveState.ResolveFailed,
                    StatusText = "Penumbra mods changed while building the source; build it again",
                };
            }

            cancellationToken.ThrowIfCancellationRequested();
            resolved.Add(usage.Key, new ResolvedCollection(result,
                result.ResourceViews.ToDictionary(static view => view.RootPath, StringComparer.OrdinalIgnoreCase)));
        }

        return resolved;
    }

    private static TemporarySourceBuildResult CreateInvalidResult(string message)
        => new(
            TemporarySourceBuildStatus.InvalidRequest,
            message,
            null,
            [],
            [new TemporarySourceBuildDiagnostic(
                Guid.Empty,
                string.Empty,
                TemporarySourceBuildDiagnosticSeverity.Error,
                message)]);

    private static bool ValidateResolution(
        ObjectSnapshot snapshot,
        ObjectCollectionResolveResult result,
        List<TemporarySourceBuildDiagnostic> diagnostics)
    {
        bool failed = !result.IsComplete || result.KeepLastGoodSnapshot
            || result.ResolveState is not (ObjectCollectionResolveState.Ready or ObjectCollectionResolveState.Inactive);
        foreach (string warning in result.Warnings)
        {
            diagnostics.Add(CreateDiagnostic(
                snapshot,
                failed ? TemporarySourceBuildDiagnosticSeverity.Error : TemporarySourceBuildDiagnosticSeverity.Warning,
                warning));
        }

        if (failed)
        {
            diagnostics.Add(CreateDiagnostic(snapshot, TemporarySourceBuildDiagnosticSeverity.Error, result.StatusText));
        }

        return !failed;
    }

    private static TemporarySourceBuildDiagnostic CreateDiagnostic(
        ObjectSnapshot snapshot,
        TemporarySourceBuildDiagnosticSeverity severity,
        string message)
        => new(
            snapshot.Id,
            snapshot.CollectionId,
            severity,
            TextUtility.TrimOrFallback(message, "temporary source build issue"));

    private static string BuildRedirectSignature(IReadOnlyList<ObjectPathRedirection> redirects)
    {
        StringBuilder builder = new();
        foreach (ObjectPathRedirection redirect in redirects)
        {
            builder
                .Append(redirect.RequestedPath)
                .Append('|')
                .Append((int)redirect.ResolvedPath.Kind)
                .Append('|')
                .Append(redirect.ResolvedPath.Path)
                .Append('\n');
        }

        return builder.ToString();
    }

    private static TemporaryObjectCollectionRedirect ToTemporaryRedirect(ObjectPathRedirection redirect)
        => new(redirect.RequestedPath, new TemporaryCollectionReplacement
        {
            Kind = redirect.ResolvedPath.Kind switch
            {
                ObjectResolvedPathKind.GamePath => TemporaryCollectionReplacementKind.GamePath,
                ObjectResolvedPathKind.LocalFile => TemporaryCollectionReplacementKind.LocalFile,
                _ => throw new ArgumentOutOfRangeException(nameof(redirect), redirect.ResolvedPath.Kind, null),
            },
            Path = redirect.ResolvedPath.Path,
        });
}
