using System.Collections.Immutable;
using Intoner.Objects.Resources;

namespace Intoner.Objects.Collections;

internal enum ObjectCollectionResolveState
{
    Ready,
    Resolving,
    WaitingForPenumbra,
    Inactive,
    ModMissing,
    ResolveFailed,
}

internal enum ObjectCollectionMaterializationState
{
    Current,
    Pending,
}

internal sealed record ObjectCollectionModSettings
{
    public string ModDirectory { get; init; } = string.Empty;
    public string ModName { get; init; } = string.Empty;
    public bool Enabled { get; init; } = true;
    public int Priority { get; init; }
    public Dictionary<string, List<string>> Settings { get; init; } = new(StringComparer.Ordinal);
}

internal enum ObjectCollectionModSettingsGroupKind
{
    Single,
    Multi,
    Combining,
}

internal readonly record struct ObjectCollectionModSettingsOption(
    string Name,
    int Priority,
    bool DefaultSelected,
    bool Selected);

internal sealed record ObjectCollectionModSettingsGroup
{
    public string Name { get; init; } = string.Empty;
    public ObjectCollectionModSettingsGroupKind Kind { get; init; }
    public bool HasOverride { get; init; }
    public IReadOnlyList<ObjectCollectionModSettingsOption> Options { get; init; } = [];
}

internal sealed record ObjectCollectionModSettingsView
{
    public ObjectCollectionResolveState ResolveState { get; init; } = ObjectCollectionResolveState.Inactive;
    public string StatusText { get; init; } = string.Empty;
    public IReadOnlyList<ObjectCollectionModSettingsGroup> Groups { get; init; } = [];
}

internal sealed record ObjectCollection
{
    public string CollectionId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public List<ObjectCollectionModSettings> Entries { get; init; } = [];
}

internal sealed record ObjectCollectionSnapshot
{
    public ObjectCollection Record { get; init; } = new();
    public ObjectCollectionResolveState ResolveState { get; init; } = ObjectCollectionResolveState.Inactive;
    public string StatusText { get; init; } = string.Empty;
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public bool KeepingLastGoodSnapshot { get; init; }
    public int RedirectCount { get; init; }
}

internal sealed record ObjectCollectionsFile
{
    public int Version { get; init; } = 1;
    public List<ObjectCollection> Collections { get; init; } = [];
}

internal readonly record struct ObjectAvailableMod(
    string ModDirectory,
    string ModName);

internal sealed record ObjectModResolveResult
{
    public ObjectCollectionResolveState ResolveState { get; init; } = ObjectCollectionResolveState.Inactive;
    public string StatusText { get; init; } = string.Empty;
    public bool KeepLastGoodSnapshot { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public IReadOnlyDictionary<string, ObjectResolvedPath> ResolvedPaths { get; init; }
        = ImmutableDictionary<string, ObjectResolvedPath>.Empty;
}


