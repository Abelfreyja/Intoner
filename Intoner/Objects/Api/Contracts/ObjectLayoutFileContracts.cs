using Intoner.Objects.Models;

namespace Intoner.Objects.Api;

/// <summary> json file payload for one exported object layout </summary>
internal sealed record ObjectLayoutFileDocument
{
    public const string DocumentKindValue = "object-layout";
    public const int CurrentFormatVersion = 3;

    public required string DocumentKind { get; init; }
    public required int FormatVersion { get; init; }
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required long Revision { get; init; }
    public required DateTime ExportedAtUtc { get; init; }
    public required DateTime CreatedAtUtc { get; init; }
    public required DateTime UpdatedAtUtc { get; init; }
    public required List<ObjectLayoutFileObject> Objects { get; init; }
    public List<ObjectFolderSnapshot> Folders { get; init; } = [];
    public List<PersistentSceneGroup> Groups { get; init; } = [];
}

/// <summary> json file payload for one saved object including editor only metadata </summary>
internal record ObjectLayoutFileObject
{
    public Guid? GroupId { get; init; }
    public string FolderPath { get; init; } = string.Empty;
    public bool Locked { get; init; }
    public required WorldObject Object { get; init; }
}

/// <summary> json file payload for the latest object workspace autosave draft </summary>
internal sealed record ObjectLayoutAutosaveDocument
{
    public const string DocumentKindValue = "object-autosave";
    public const int CurrentFormatVersion = 3;

    public required string DocumentKind { get; init; }
    public required int FormatVersion { get; init; }
    public required DateTime SavedAtUtc { get; init; }
    public required long PersistentRevision { get; init; }
    public Guid? CreationLayoutId { get; init; }
    public required List<ObjectLayoutAutosaveOwner> Layouts { get; init; }
    public required List<ObjectLayoutAutosaveObject> Objects { get; init; }
    public List<ObjectFolderSnapshot> StandaloneFolders { get; init; } = [];
    public List<PersistentSceneGroup> StandaloneGroups { get; init; } = [];
}

/// <summary> preserves enabled layout owners including layouts with no objects </summary>
internal sealed record ObjectLayoutAutosaveOwner
{
    public List<PersistentSceneGroup> Groups { get; init; } = [];
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public List<ObjectFolderSnapshot> Folders { get; init; } = [];
}

/// <summary> json file payload for one autosaved object including layout ownership </summary>
internal sealed record ObjectLayoutAutosaveObject : ObjectLayoutFileObject
{
    public Guid? LayoutId { get; init; }
}
