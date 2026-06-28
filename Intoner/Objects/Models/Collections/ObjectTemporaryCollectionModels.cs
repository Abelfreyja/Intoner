namespace Intoner.Objects.Models;

internal enum ObjectTemporaryCollectionReplacementKind
{
    GamePath = 1,
    LocalFile = 2,
    Memory = 3,
}

internal sealed record ObjectTemporaryCollectionReplacementData
{
    public ObjectTemporaryCollectionReplacementKind Kind { get; init; }
    public string Path { get; init; } = string.Empty;
    public byte[] Data { get; init; } = [];
}

internal sealed record ObjectTemporaryCollectionRedirectData
{
    public string RequestedPath { get; init; } = string.Empty;
    public ObjectTemporaryCollectionReplacementData Replacement { get; init; } = new();
}

internal sealed record ObjectTemporaryCollectionData
{
    public string CollectionId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public IReadOnlyList<ObjectTemporaryCollectionRedirectData> Redirects { get; init; } = [];
}

internal sealed record ObjectTemporaryCollectionSourceSnapshot
{
    public string SourceKey { get; init; } = string.Empty;
    public Guid SourceSessionId { get; init; }
    public string Name { get; init; } = string.Empty;
    public long Revision { get; init; }
    public IReadOnlyList<ObjectTemporaryCollectionData> Collections { get; init; } = [];
}

