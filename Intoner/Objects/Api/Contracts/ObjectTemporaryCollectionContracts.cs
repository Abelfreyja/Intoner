using MessagePack;

namespace Intoner.Objects.Api;

/// <summary> temporary object collection replacement kind </summary>
public enum TemporaryCollectionReplacementKind
{
    GamePath = 1,
    LocalFile = 2,
    Memory = 3,
}

/// <summary> one temporary object collection replacement </summary>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record TemporaryCollectionReplacement
{
    /// <summary> replacement kind </summary>
    public TemporaryCollectionReplacementKind Kind { get; init; }

    /// <summary> replacement game path, local file path, or memory resource game path </summary>
    public string Path { get; init; } = string.Empty;

    /// <summary> memory resource bytes for memory replacements </summary>
    public byte[] Data { get; init; } = [];
}

/// <summary> one temporary object collection redirect </summary>
/// <param name="RequestedPath"> requested game path to replace </param>
/// <param name="Replacement"> replacement path </param>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record TemporaryObjectCollectionRedirect(
    string RequestedPath,
    TemporaryCollectionReplacement Replacement);

/// <summary> one temporary object collection payload </summary>
/// <param name="CollectionId"> collection id referenced by temporary objects </param>
/// <param name="Name"> display name for the temporary collection </param>
/// <param name="Redirects"> redirect set for this collection </param>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record TemporaryObjectCollection(
    string CollectionId,
    string Name,
    IReadOnlyList<TemporaryObjectCollectionRedirect> Redirects);

/// <summary> full temporary collection set replacement </summary>
/// <param name="SourceKey"> temporary source key </param>
/// <param name="SourceSessionId"> source session id; use 'Guid.Empty' to reuse the current session for this source </param>
/// <param name="Name"> temporary source display name, or empty to keep the current name </param>
/// <param name="Revision"> source revision, or '0' to advance it locally </param>
/// <param name="Collections"> full replacement collection list for the source </param>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record TemporaryCollectionsApplyRequest(
    string SourceKey,
    Guid SourceSessionId,
    string Name,
    long Revision,
    IReadOnlyList<TemporaryObjectCollection> Collections);

/// <summary> temporary collection upsert </summary>
/// <param name="SourceKey"> temporary source key </param>
/// <param name="SourceSessionId"> source session id; use 'Guid.Empty' to reuse the current session for this source </param>
/// <param name="Name"> temporary source display name, or empty to keep the current name </param>
/// <param name="Revision"> source revision, or '0' to advance it locally </param>
/// <param name="Collection"> collection payload to create or update in the source </param>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record TemporaryCollectionUpsert(
    string SourceKey,
    Guid SourceSessionId,
    string Name,
    long Revision,
    TemporaryObjectCollection Collection);

/// <summary> temporary collection removal request </summary>
/// <param name="SourceKey"> temporary source key </param>
/// <param name="SourceSessionId"> source session id; use 'Guid.Empty' to reuse the current session for this source </param>
/// <param name="CollectionIds"> one or more collection ids to remove from the source </param>
/// <param name="Revision"> source revision, or '0' to advance it locally </param>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record TemporaryCollectionsRemoveRequest(
    string SourceKey,
    Guid SourceSessionId,
    IReadOnlyList<string> CollectionIds,
    long Revision);

