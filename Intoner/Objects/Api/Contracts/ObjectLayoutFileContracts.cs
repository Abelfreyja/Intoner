namespace Intoner.Objects.Api;

/// <summary> json file payload for one exported object layout </summary>
internal sealed record ObjectLayoutFileDocument
{
    public const string DocumentKindValue = "object-layout";
    public const int CurrentFormatVersion = 1;

    public required string DocumentKind { get; init; }
    public required int FormatVersion { get; init; }
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required DateTime ExportedAtUtc { get; init; }
    public required DateTime CreatedAtUtc { get; init; }
    public required DateTime UpdatedAtUtc { get; init; }
    public required List<ObjectLayoutFileObject> Objects { get; init; }
    public required List<string> Folders { get; init; }
    public required Dictionary<string, string> FolderColors { get; init; }
}

/// <summary> json file payload for one saved object including editor only metadata </summary>
internal sealed record ObjectLayoutFileObject
{
    public required string FolderPath { get; init; }
    public required bool Locked { get; init; }
    public required WorldObject Object { get; init; }
}

/// <summary> json file payload for the latest object workspace autosave draft </summary>
internal sealed record ObjectLayoutAutosaveDocument
{
    public const string DocumentKindValue = "object-autosave";
    public const int CurrentFormatVersion = 1;

    public required string DocumentKind { get; init; }
    public required int FormatVersion { get; init; }
    public required DateTime SavedAtUtc { get; init; }
    public required long PersistentRevision { get; init; }
    public required Guid? DefaultLayoutId { get; init; }
    public required string Name { get; init; }
    public required List<ObjectLayoutAutosaveObject> Objects { get; init; }
    public required List<string> Folders { get; init; }
    public required Dictionary<string, string> FolderColors { get; init; }
}

/// <summary> json file payload for one autosaved object including layout ownership </summary>
internal sealed record ObjectLayoutAutosaveObject
{
    public Guid? LayoutId { get; init; }
    public required string FolderPath { get; init; }
    public required bool Locked { get; init; }
    public required WorldObject Object { get; init; }
}
