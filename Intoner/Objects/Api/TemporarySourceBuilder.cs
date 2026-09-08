using System.Security.Cryptography;
using System.Text;
using Intoner.Objects.Collections;
using Intoner.Objects.Models;
using Intoner.Objects.Resources;
using Intoner.Objects.Utils;

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
    private readonly record struct ResolveCacheKey(
        string CollectionId,
        ObjectKind Kind,
        string RootPath);

    private sealed record ResolvedCollection(
        ObjectCollectionResolveResult Result,
        IReadOnlyList<ObjectPathRedirection> Redirects);

    private sealed record GeneratedCollection(
        string CollectionId,
        ObjectTemporaryCollectionData Collection);

    private readonly IObjectCollectionManager  _collectionManager;
    private readonly IObjectCollectionResolver _collectionResolver;

    public TemporarySourceBuilder(
        IObjectCollectionManager collectionManager,
        IObjectCollectionResolver collectionResolver)
    {
        _collectionManager  = collectionManager;
        _collectionResolver = collectionResolver;
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

        if (!ObjectApiMapper.TryToDetachedSnapshots(request.Objects, out List<ObjectSnapshot> snapshots))
        {
            return CreateInvalidResult("temporary source build contains invalid objects");
        }

        string sourceName = TextUtility.TrimOrEmpty(request.Name);
        List<ObjectSnapshot> payloadSnapshots = new(snapshots.Count);
        List<TemporarySourceBuildDiagnostic> diagnostics = [];
        Dictionary<ResolveCacheKey, ResolvedCollection> resolvedCollectionCache = [];
        Dictionary<string, GeneratedCollection> generatedCollectionsBySignature = new(StringComparer.Ordinal);
        SortedSet<string> localFilePaths = new(StringComparer.OrdinalIgnoreCase);

        foreach (ObjectSnapshot snapshot in snapshots)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string authoredCollectionId = ObjectCollectionKeyUtility.NormalizeCollectionId(snapshot.CollectionId);
            if (authoredCollectionId.Length == 0)
            {
                AddWithoutCollection(payloadSnapshots, snapshot);
                continue;
            }

            if (!_collectionManager.TryGetCollection(authoredCollectionId, out ObjectCollectionSnapshot authoredCollection))
            {
                diagnostics.Add(CreateDiagnostic(
                    snapshot,
                    authoredCollectionId,
                    TemporarySourceBuildDiagnosticSeverity.Error,
                    "assigned object collection was not found"));
                continue;
            }

            string rootPath = ObjectSnapshotUtility.GetRootResourcePath(snapshot);
            if (rootPath.Length == 0)
            {
                diagnostics.Add(CreateDiagnostic(
                    snapshot,
                    authoredCollectionId,
                    TemporarySourceBuildDiagnosticSeverity.Info,
                    "object kind has no resource collection scope"));
                AddWithoutCollection(payloadSnapshots, snapshot);
                continue;
            }

            ResolveCacheKey resolveCacheKey = CreateResolveCacheKey(authoredCollectionId, snapshot.Kind, rootPath);
            if (!resolvedCollectionCache.TryGetValue(resolveCacheKey, out ResolvedCollection? resolvedCollection))
            {
                ObjectCollectionResolveResult result = await _collectionResolver
                    .ResolveAsync(authoredCollection.Record, [snapshot], cancellationToken)
                    .ConfigureAwait(false);
                resolvedCollection = new ResolvedCollection(result, ObjectPathRedirectionUtility.CreateStableList(result.Redirects));
                resolvedCollectionCache[resolveCacheKey] = resolvedCollection;
            }

            AddResolveDiagnostics(snapshot, authoredCollectionId, resolvedCollection.Result, diagnostics);
            if (HasResolveFailure(resolvedCollection.Result))
            {
                continue;
            }

            if (resolvedCollection.Redirects.Count == 0)
            {
                AddWithoutCollection(payloadSnapshots, snapshot);
                continue;
            }

            string redirectSignature = BuildRedirectSignature(resolvedCollection.Redirects);
            if (!generatedCollectionsBySignature.TryGetValue(redirectSignature, out GeneratedCollection? generatedCollection))
            {
                string temporaryCollectionId = CreateTemporaryCollectionId(redirectSignature);
                generatedCollection = new GeneratedCollection(
                    temporaryCollectionId,
                    new ObjectTemporaryCollectionData
                    {
                        CollectionId = temporaryCollectionId,
                        Name = TextUtility.TrimOrFallback(authoredCollection.Record.Name, temporaryCollectionId),
                        Redirects = resolvedCollection.Redirects
                            .Select(ToTemporaryRedirect)
                            .ToList(),
                    });
                generatedCollectionsBySignature[redirectSignature] = generatedCollection;
            }

            localFilePaths.UnionWith(resolvedCollection.Redirects
                .Where(static redirect => redirect.ResolvedPath.IsLocalFile)
                .Select(static redirect => redirect.ResolvedPath.Path));

            payloadSnapshots.Add(snapshot with { CollectionId = generatedCollection.CollectionId });
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

        List<WorldObject> payloadObjects = payloadSnapshots
            .Select(ObjectApiMapper.ToWorldObject)
            .ToList();
        List<TemporaryObjectCollection> payloadCollections = generatedCollectionsBySignature.Values
            .OrderBy(static collection => collection.CollectionId, StringComparer.OrdinalIgnoreCase)
            .Select(static collection => ObjectApiMapper.ToTemporaryCollection(collection.Collection))
            .ToList();

        TemporarySourceBuildStatus status = diagnostics.Any(static diagnostic =>
                diagnostic.Severity == TemporarySourceBuildDiagnosticSeverity.Warning)
            ? TemporarySourceBuildStatus.CompletedWithWarnings
            : TemporarySourceBuildStatus.Success;

        return new TemporarySourceBuildResult(
            status,
            BuildSummary(payloadObjects.Count, payloadCollections.Count, localFilePaths.Count, diagnostics.Count),
            new TemporarySourceApplyRequest(
                sourceId,
                request.SessionId,
                sourceName,
                request.Revision,
                payloadObjects,
                payloadCollections),
            localFilePaths.ToList(),
            diagnostics);
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

    private static void AddResolveDiagnostics(
        ObjectSnapshot snapshot,
        string collectionId,
        ObjectCollectionResolveResult result,
        List<TemporarySourceBuildDiagnostic> diagnostics)
    {
        bool failed = HasResolveFailure(result);
        foreach (string warning in result.Warnings)
        {
            diagnostics.Add(CreateDiagnostic(
                snapshot,
                collectionId,
                failed ? TemporarySourceBuildDiagnosticSeverity.Error : TemporarySourceBuildDiagnosticSeverity.Warning,
                warning));
        }

        if (!failed)
        {
            return;
        }

        diagnostics.Add(CreateDiagnostic(
            snapshot,
            collectionId,
            TemporarySourceBuildDiagnosticSeverity.Error,
            result.StatusText));
    }

    private static bool HasResolveFailure(ObjectCollectionResolveResult result)
        => !result.IsComplete || result.KeepLastGoodSnapshot
         || result.ResolveState is not (ObjectCollectionResolveState.Ready or ObjectCollectionResolveState.Inactive);

    private static TemporarySourceBuildDiagnostic CreateDiagnostic(
        ObjectSnapshot snapshot,
        string collectionId,
        TemporarySourceBuildDiagnosticSeverity severity,
        string message)
        => new(
            snapshot.Id,
            collectionId,
            severity,
            TextUtility.TrimOrFallback(message, "temporary source build issue"));

    private static void AddWithoutCollection(List<ObjectSnapshot> snapshots, ObjectSnapshot snapshot)
        => snapshots.Add(snapshot with { CollectionId = string.Empty });

    private static ResolveCacheKey CreateResolveCacheKey(string collectionId, ObjectKind kind, string rootPath)
        => new(collectionId, kind, rootPath.ToLowerInvariant());

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

    private static string CreateTemporaryCollectionId(string redirectSignature)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(redirectSignature), hash);
        return $"tempcoll_{Convert.ToHexString(hash[..16]).ToLowerInvariant()}";
    }

    private static ObjectTemporaryCollectionRedirectData ToTemporaryRedirect(ObjectPathRedirection redirect)
        => new()
        {
            RequestedPath = redirect.RequestedPath,
            Replacement = ToTemporaryReplacement(redirect.ResolvedPath),
        };

    private static ObjectTemporaryCollectionReplacementData ToTemporaryReplacement(ObjectResolvedPath resolvedPath)
        => resolvedPath.Kind switch
        {
            ObjectResolvedPathKind.GamePath => new ObjectTemporaryCollectionReplacementData
            {
                Kind = ObjectTemporaryCollectionReplacementKind.GamePath,
                Path = resolvedPath.Path,
            },
            ObjectResolvedPathKind.LocalFile => new ObjectTemporaryCollectionReplacementData
            {
                Kind = ObjectTemporaryCollectionReplacementKind.LocalFile,
                Path = resolvedPath.Path,
            },
            _ => throw new ArgumentOutOfRangeException(nameof(resolvedPath), resolvedPath.Kind, null),
        };

    private static string BuildSummary(int objectCount, int collectionCount, int localFileCount, int diagnosticCount)
        => diagnosticCount == 0
            ? $"built {objectCount} objects, {collectionCount} temporary collections, and {localFileCount} local files"
            : $"built {objectCount} objects, {collectionCount} temporary collections, and {localFileCount} local files with {diagnosticCount} diagnostics";
}

