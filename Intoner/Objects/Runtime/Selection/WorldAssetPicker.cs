using Dalamud.Plugin.Services;
using Intoner.Objects.Models;
using Intoner.Objects.Utils;
using Intoner.Utils;
using System.Numerics;

namespace Intoner.Scene;

internal enum WorldAssetPickStatus
{
    None,
    Loading,
    Unavailable,
    Hit,
}

internal readonly record struct WorldAssetPickResult(WorldAssetPickStatus Status, WorldAsset? Asset = null, SceneSurfaceHit Hit = default)
{
    public static readonly WorldAssetPickResult None = new(WorldAssetPickStatus.None);
}

internal sealed record WorldAssetPickState(bool IsPicking, WorldAssetPickResult Hover)
{
    public WorldAsset? SelectedAsset { get; init; }
    public bool HasLiveSelection { get; init; }
    public WorldAsset? LiveSelection => HasLiveSelection ? SelectedAsset : null;
    public IReadOnlyList<WorldAsset> Nearby { get; init; } = [];
    public IReadOnlyList<WorldAsset> Recent { get; init; } = [];
    public Vector3 NearbyOrigin { get; init; }
    public bool NearbyFromCamera { get; init; }
    public long SelectedInstanceId => SelectedAsset?.InstanceId ?? 0;

    public string? SelectedPath => SelectedAsset?.Path;
}

internal sealed class WorldAssetPicker(IFramework framework, IWorldAssetSource source, ISceneSelectionGeometryProvider geometry, IObjectTable objectTable)
{
    private const int RecentLimit = 32;
    private readonly List<WorldAsset> _assets = [];
    private readonly List<WorldAsset> _nearbyCapture = [];
    private float[] _nearbyDistances = [];
    private readonly List<(WorldAsset Asset, float Distance)> _candidates = [];
    private WorldAssetPickState _state = new(false, WorldAssetPickResult.None);

    public WorldAssetPickState State => Volatile.Read(ref _state);

    public void Begin()
        => FrameworkThreadUtility.Run(framework, () => Publish(State with { IsPicking = true, Hover = WorldAssetPickResult.None }));

    public void Cancel()
        => FrameworkThreadUtility.Run(framework, () => Publish(State with { IsPicking = false, Hover = WorldAssetPickResult.None }));

    public WorldAssetPickResult Update(ScenePointerRay ray)
        => FrameworkThreadUtility.Run(framework, () => UpdateCore(ray));

    private WorldAssetPickResult SetHover(WorldAssetPickResult result)
    {
        if (State.Hover != result)
        {
            Publish(State with { Hover = result });
        }
        return result;
    }

    public WorldAssetPickResult UpdateScreen(Vector2 viewportPosition, Vector2 viewportSize, Vector2 pointerPosition)
        => FrameworkThreadUtility.Run(framework, () => UpdateScreenCore(viewportPosition, viewportSize, pointerPosition));

    public bool ConfirmScreen(Vector2 viewportPosition, Vector2 viewportSize, Vector2 pointerPosition)
        => FrameworkThreadUtility.Run(framework, () => ConfirmCore(UpdateScreenCore(viewportPosition, viewportSize, pointerPosition)));

    private WorldAssetPickResult UpdateScreenCore(Vector2 viewportPosition, Vector2 viewportSize, Vector2 pointerPosition)
    {
        if (!State.IsPicking)
        {
            return WorldAssetPickResult.None;
        }

        return SceneScreenRaycaster.TryBuildScreenRay(viewportPosition, viewportSize, pointerPosition, out Vector3 origin, out Vector3 direction)
            ? UpdateCore(new(origin, direction))
            : SetHover(new(WorldAssetPickStatus.Unavailable));
    }

    public bool Confirm(ScenePointerRay ray)
        => FrameworkThreadUtility.Run(framework, () => ConfirmCore(UpdateCore(ray)));

    private bool ConfirmCore(WorldAssetPickResult result)
    {
        if (!State.IsPicking || result is not { Status: WorldAssetPickStatus.Hit, Asset: { } asset })
        {
            return false;
        }

        SelectCore(asset, asset);
        return true;
    }

    private WorldAssetPickResult UpdateCore(ScenePointerRay ray)
    {
        WorldAssetPickResult result = WorldAssetPickResult.None;

        if (!State.IsPicking)
        {
            return result;
        }

        _assets.Clear();
        _candidates.Clear();

        if (!NumericsUtility.IsFinite(ray.Origin) || !NumericsUtility.TryNormalize(ray.Direction, out var direction))
        {
            return SetHover(result);
        }

        ray = ray with { Direction = direction };

        if (!source.Capture(ray, _assets, State.LiveSelection?.InstanceId ?? 0))
        {
            _assets.Clear();
            result = new(WorldAssetPickStatus.Unavailable);
        }
        else
        {
            foreach (WorldAsset asset in _assets)
            {
                if (asset.HasBounds && SceneBoundsRaycaster.TryIntersectOrientedBoundsNormalized(ray, asset.Bounds, out float distance))
                {
                    _candidates.Add((asset, distance));
                }
            }

            _candidates.Sort(static (left, right) => left.Distance.CompareTo(right.Distance));
            result = PickNearest(ray);
        }

        UpdateSelection();
        _assets.Clear();
        _candidates.Clear();
        return SetHover(result);
    }

    private WorldAssetPickResult PickNearest(ScenePointerRay ray)
    {
        WorldAssetPickResult result = WorldAssetPickResult.None;
        float hitDistance = float.PositiveInfinity;

        foreach ((WorldAsset asset, float distance) in _candidates)
        {
            if (distance >= hitDistance)
            {
                break;
            }

            if (asset.UnavailableReason != WorldAssetUnavailableReason.None)
            {
                return new(WorldAssetPickStatus.Unavailable, asset);
            }

            if (asset.Kind == ObjectKind.Vfx)
            {
                hitDistance = distance;
                result = new(WorldAssetPickStatus.Hit, asset,
                    new SceneSurfaceHit(ray.Origin + ray.Direction * distance, -ray.Direction, Distance: distance));
                continue;
            }

            SceneGeometryAvailability availability = geometry.RequestRaycastGeometry(asset.GeometryPath ?? asset.Path, out SceneSelectionGeometry triangles);

            if (availability != SceneGeometryAvailability.Ready)
            {
                return new(availability == SceneGeometryAvailability.Loading
                    ? WorldAssetPickStatus.Loading : WorldAssetPickStatus.Unavailable, asset);
            }

            if (SceneGeometryRaycaster.TryCreateTarget(triangles, asset.Transform, out SceneGeometryRaycastTarget target)
                && SceneGeometryRaycaster.TryRaycastNormalized(target, ray, hitDistance, out SceneSurfaceHit hit))
            {
                hitDistance = hit.Distance;
                result = new(WorldAssetPickStatus.Hit, asset, hit);
            }
        }

        return result;
    }

    public void RefreshNearby()
        => FrameworkThreadUtility.Run(framework, () => RefreshNearbyCore(State.NearbyFromCamera));

    public void ToggleNearbyOrigin()
        => FrameworkThreadUtility.Run(framework, () => RefreshNearbyCore(!State.NearbyFromCamera));

    private void RefreshNearbyCore(bool fromCamera)
    {
        Vector3? origin;
        if (fromCamera)
        {
            origin = SceneViewportProjection.TryGetEditorCameraPosition(out Vector3 cameraPosition) ? cameraPosition : null;
        }
        else
        {
            origin = objectTable.LocalPlayer?.Position;
        }

        _assets.Clear();
        if (origin is not { } referencePoint || !NumericsUtility.IsFinite(referencePoint))
        {
            _nearbyCapture.Clear();
            Publish(State with { Nearby = [], NearbyFromCamera = fromCamera });
            return;
        }

        if (!source.Capture(null, _assets))
        {
            _assets.Clear();
            _nearbyCapture.Clear();
            Publish(State with { Nearby = [], NearbyFromCamera = fromCamera, HasLiveSelection = false });
            return;
        }

        UpdateSelection();
        _assets.RemoveAll(static asset => asset.UnavailableReason != WorldAssetUnavailableReason.None);
        WorldAssetPickState state = State;
        bool changed = state.NearbyFromCamera != fromCamera || state.NearbyOrigin != referencePoint || _nearbyCapture.Count != _assets.Count;
        for (int index = 0; !changed && index < _assets.Count; ++index)
        {
            changed = !ReferenceEquals(_nearbyCapture[index], _assets[index]);
        }

        if (changed)
        {
            _nearbyCapture.Clear();
            _nearbyCapture.AddRange(_assets);
            WorldAsset[] nearby = _assets.ToArray();
            if (_nearbyDistances.Length < nearby.Length)
            {
                Array.Resize(ref _nearbyDistances, nearby.Length);
            }

            for (int index = 0; index < nearby.Length; ++index)
            {
                _nearbyDistances[index] = Vector3.DistanceSquared(nearby[index].Transform.Translation, referencePoint);
            }

            Array.Sort(_nearbyDistances, nearby, 0, nearby.Length);
            Publish(state with { Nearby = nearby, NearbyOrigin = referencePoint, NearbyFromCamera = fromCamera });
        }

        _assets.Clear();
    }

    public void Select(WorldAsset asset)
        => FrameworkThreadUtility.Run(framework, () =>
        {
            _assets.Clear();
            WorldAsset? current = source.Capture(null, _assets, asset.InstanceId)
                ? _assets.Find(candidate => candidate.InstanceId == asset.InstanceId
                    && candidate.UnavailableReason == WorldAssetUnavailableReason.None) : null;
            SelectCore(asset, current);
            _assets.Clear();
        });

    private void SelectCore(WorldAsset picked, WorldAsset? current)
    {
        List<WorldAsset> recent = new(State.Recent);
        recent.RemoveAll(entry => entry.InstanceId == picked.InstanceId);
        recent.Insert(0, current ?? picked);
        if (recent.Count > RecentLimit)
        {
            recent.RemoveAt(RecentLimit);
        }

        Publish(State with
        {
            IsPicking = false, Hover = WorldAssetPickResult.None,
            SelectedAsset = current ?? picked, HasLiveSelection = current is not null,
            Recent = recent.ToArray(),
        });
    }

    public void RefreshSelection()
    {
        if (State.LiveSelection is not null)
        {
            FrameworkThreadUtility.Run(framework, RefreshSelectionCore);
        }
    }

    private void RefreshSelectionCore()
    {
        if (State.LiveSelection is not { } selected)
        {
            return;
        }

        _assets.Clear();
        if (!source.Capture(null, _assets, selected.InstanceId))
        {
            _assets.Clear();
        }

        UpdateSelection();
        _assets.Clear();
    }

    private void UpdateSelection()
    {
        if (State.LiveSelection is not { } selected)
        {
            return;
        }

        WorldAsset? current = null;
        foreach (WorldAsset asset in _assets)
        {
            if (asset.InstanceId == selected.InstanceId)
            {
                current = asset;
                break;
            }
        }

        if (!ReferenceEquals(current, selected))
        {
            Publish(State with { SelectedAsset = current ?? State.SelectedAsset, HasLiveSelection = current is not null });
        }
    }

    public void ClearScene()
        => FrameworkThreadUtility.Run(framework, () =>
        {
            source.Clear();
            _nearbyCapture.Clear();
            Publish(State with { IsPicking = false, Hover = WorldAssetPickResult.None, HasLiveSelection = false, Nearby = [] });
        });

    private void Publish(WorldAssetPickState state)
        => Volatile.Write(ref _state, state);
}
