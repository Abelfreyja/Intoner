using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;

namespace Intoner.Scene;

/// <summary> identifies the current active, persisted, and bounds states </summary>
[StructLayout(LayoutKind.Auto)]
internal readonly record struct SceneRevisions(long Active, long Persistent, long Bounds);

/// <summary> owns one concrete scene item domain and exposes it to shared scene systems </summary>
internal interface ISceneItemDomain
{
    /// <summary> gets the current active, persisted, and bounds revisions </summary>
    SceneRevisions Revisions { get; }

    /// <summary> gets the manipulation behavior owned by this domain </summary>
    SceneItemManipulationPolicy Manipulation { get; }

    /// <summary> gets all persisted items owned by this domain </summary>
    IReadOnlyList<SceneItemSnapshot> GetPlacedItems();

    /// <summary> gets all active items owned by this domain </summary>
    IReadOnlyList<SceneItemSnapshot> GetActiveItems();

    /// <summary> gets all active runtime items owned by this domain </summary>
    IReadOnlyList<ISceneItemRuntime> GetRuntimeItems();

    /// <summary> gets the current bounds published by active items in this domain </summary>
    IReadOnlyList<SceneItemBoundsSnapshot> GetBoundsSnapshots();

    /// <summary> checks whether this domain owns the given snapshot type </summary>
    bool CanHandle(SceneItemSnapshot snapshot);

    /// <summary> applies ordered create, update, and remove transitions and reports failed recovery separately </summary>
    SceneMutationStatus ApplyChanges(IReadOnlyList<SceneItemSnapshotChange> changes);

    /// <summary> applies a reversible manipulation to an unchanged active runtime without persistence or recreation </summary>
    /// <param name="before"> the expected active snapshot </param>
    /// <param name="after"> the requested transform or supported surface attachment change </param>
    /// <param name="applied"> the sanitized snapshot on success </param>
    /// <returns> applied on success, rejected without changing the prior runtime, or recovery required </returns>
    SceneMutationStatus PreviewChange(SceneItemSnapshot before, SceneItemSnapshot after, out SceneItemSnapshot applied);

    /// <summary> requests runtime reconciliation from persisted state after an unrecovered or superseded preview </summary>
    void RequestPreviewRecovery();

    /// <summary> prepares duplicates without mutating persisted or active scene state </summary>
    bool TryCreateDuplicates(
        IReadOnlyList<SceneItemSnapshot> snapshots,
        out IReadOnlyList<SceneItemSnapshot> duplicates);
}

[StructLayout(LayoutKind.Auto)]
internal readonly record struct SceneItemOwnership(
    ISceneItemDomain Domain,
    SceneItemSnapshot Snapshot);

/// <summary> resolves scene item ownership across registered domains </summary>
internal sealed class SceneItemDomains
{
    private readonly ILogger _logger;

    public SceneItemDomains(ILogger logger, IEnumerable<ISceneItemDomain> domains)
    {
        _logger = logger;
        All = domains.ToList();
    }

    public IReadOnlyList<ISceneItemDomain> All { get; }

    public bool TryResolve(SceneItemSnapshot snapshot, out ISceneItemDomain domain)
    {
        ISceneItemDomain? resolved = null;
        foreach (ISceneItemDomain candidate in All)
        {
            if (!candidate.CanHandle(snapshot))
            {
                continue;
            }

            if (resolved is not null)
            {
                _logger.LogError(
                    "multiple scene item domains accept snapshot type {SnapshotType}",
                    snapshot.GetType().Name);
                domain = null!;
                return false;
            }

            resolved = candidate;
        }

        if (resolved is null)
        {
            _logger.LogError("no scene item domain accepts snapshot type {SnapshotType}", snapshot.GetType().Name);
            domain = null!;
            return false;
        }

        domain = resolved;
        return true;
    }

    public bool TryResolve(SceneItemSnapshotChange change, out ISceneItemDomain domain)
    {
        domain = null!;
        if (change.Before is null)
        {
            return change.After is not null && TryResolve(change.After, out domain);
        }

        if (change.After is null)
        {
            return TryResolve(change.Before, out domain);
        }

        if (!TryResolve(change.Before, out ISceneItemDomain beforeDomain)
            || !TryResolve(change.After, out ISceneItemDomain afterDomain))
        {
            return false;
        }

        if (!ReferenceEquals(beforeDomain, afterDomain))
        {
            _logger.LogError(
                "scene item {ItemId} cannot change from domain {BeforeDomain} to {AfterDomain}",
                change.Before.Id,
                beforeDomain.GetType().Name,
                afterDomain.GetType().Name);
            return false;
        }

        domain = beforeDomain;
        return true;
    }

    public bool TryCapturePlacedItems(out Dictionary<Guid, SceneItemOwnership> items)
    {
        items = [];
        foreach (ISceneItemDomain domain in All)
        {
            foreach (SceneItemSnapshot snapshot in domain.GetPlacedItems())
            {
                if (snapshot.Id != Guid.Empty
                    && domain.CanHandle(snapshot)
                    && items.TryAdd(snapshot.Id, new SceneItemOwnership(domain, snapshot)))
                {
                    continue;
                }

                _logger.LogError(
                    "scene item domain {DomainType} returned invalid or duplicate persisted item {ItemId}",
                    domain.GetType().Name,
                    snapshot.Id);
                items = [];
                return false;
            }
        }

        return true;
    }
}
