using System.Runtime.InteropServices;

namespace Intoner.Scene;

/// <summary> common persisted state for one placed scene item </summary>
internal abstract record SceneItemSnapshot
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public bool Locked { get; init; }
    public bool Visible { get; init; } = true;
    public SceneTransform Transform { get; init; } = new();
    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
    public SceneCreationContext CreatedIn { get; init; } = new();
}

/// <summary> one atomic scene item snapshot transition </summary>
[StructLayout(LayoutKind.Auto)]
internal readonly record struct SceneItemSnapshotChange(SceneItemSnapshot? Before, SceneItemSnapshot? After)
{
    public bool HasChange => !Equals(Before, After);

    public bool TryGetSnapshots<TSnapshot>(out TSnapshot? before, out TSnapshot? after)
        where TSnapshot : SceneItemSnapshot
    {
        before = Before as TSnapshot;
        after = After as TSnapshot;
        return (Before is null || before is not null)
            && (After is null || after is not null)
            && (before is null || after is null || before.Id == after.Id);
    }

    public SceneItemSnapshotChange Reverse()
        => new()
        {
            Before = After,
            After = Before,
        };
}
