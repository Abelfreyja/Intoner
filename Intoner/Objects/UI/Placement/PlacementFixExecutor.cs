using Intoner.Objects.Models;
using Intoner.Objects.Runtime;
using Intoner.Objects.UI.Services;

namespace Intoner.Objects.UI;

internal sealed class PlacementFixExecutor(
    PlacementFixService fixService,
    IHistoryCoordinator historyCoordinator)
{
    public bool TryApply(ObjectSnapshot snapshot, PlacementFixProposal fix)
    {
        if (fix.ObjectId != snapshot.Id
            || !fixService.TryBuildFixedSnapshot(snapshot, fix.Kind, out ObjectSnapshot fixedSnapshot)
            || Equals(snapshot, fixedSnapshot))
        {
            return false;
        }

        return historyCoordinator.TryApplySelectedSnapshotUpdate(
            ResolveHistoryKind(fix.Kind),
            fix.Label,
            [snapshot],
            _ => fixedSnapshot);
    }

    private static ObjectHistoryKind ResolveHistoryKind(PlacementFixKind kind)
        => kind switch
        {
            PlacementFixKind.SnapToSurface         => ObjectHistoryKind.Move,
            PlacementFixKind.MoveToPlayerPlacement => ObjectHistoryKind.Move,
            PlacementFixKind.ClearAttachmentParent => ObjectHistoryKind.Organization,
            _                                      => ObjectHistoryKind.Organization,
        };
}

