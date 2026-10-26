using System.Collections.Immutable;
using System.Collections.ObjectModel;

namespace Intoner.Objects.Models;

internal sealed record ObjectFolderSceneState
{
    public IReadOnlyList<ObjectFolderSnapshot> StandaloneFolders
    {
        get;
        init => field = CopyFolders(value);
    } = ImmutableArray<ObjectFolderSnapshot>.Empty;
    public IReadOnlyDictionary<Guid, IReadOnlyList<ObjectFolderSnapshot>> LayoutFolders
    {
        get;
        init => field = new ReadOnlyDictionary<Guid, IReadOnlyList<ObjectFolderSnapshot>>(
            value.ToDictionary(static owner => owner.Key,
                static owner => CopyFolders(owner.Value)));
    } = ImmutableDictionary<Guid, IReadOnlyList<ObjectFolderSnapshot>>.Empty;

    private static IReadOnlyList<ObjectFolderSnapshot> CopyFolders(IReadOnlyList<ObjectFolderSnapshot> folders)
        => folders is ImmutableArray<ObjectFolderSnapshot> ? folders : folders.ToImmutableArray();
}
