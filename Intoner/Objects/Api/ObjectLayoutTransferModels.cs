using Intoner.Objects.Models;
using Intoner.Objects.Runtime;

namespace Intoner.Objects.Api;

#pragma warning disable MA0048 // layout transfer boundary types stay colocated

internal enum ObjectLayoutFileKind
{
    ObjectLayout,
    MakePlaceLayout,
}

internal readonly record struct ObjectLayoutTransferResult(bool Success, ObjectLayoutSnapshot? Layout, string Message)
{
    public IReadOnlyList<string> RecoveryNotes { get; init; } = [];

    public static ObjectLayoutTransferResult Failure(string message)
        => new(false, null, message);
}

internal sealed record ObjectLayoutImportPayload(
    string Name,
    IReadOnlyList<ObjectSnapshot> Snapshots,
    IReadOnlyList<ObjectFolderSnapshot> Folders,
    string SuccessMessage)
{
    public IReadOnlyList<string> RecoveryNotes { get; init; } = [];
    public ObjectRuntimeLocationContext? RequiredLocation { get; init; }
    public IReadOnlyList<SceneGroupSnapshot> Groups { get; init; } = [];
}

#pragma warning restore MA0048
