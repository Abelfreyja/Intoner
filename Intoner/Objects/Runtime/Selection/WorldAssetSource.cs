using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using Intoner.Objects.Assets;
using Intoner.Objects.Interop;
using Intoner.Objects.Models;
using Intoner.Objects.Resources;
using Intoner.Objects.Runtime;
using Intoner.Objects.Utils;
using System.Numerics;
using AxisAlignedBounds = FFXIVClientStructs.FFXIV.Common.Math.AxisAlignedBounds;
using SceneObject = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Object;

namespace Intoner.Scene;

internal enum WorldAssetUnavailableReason
{
    None,
    UnresolvedResource,
    Animated,
}

internal sealed record WorldAsset(ObjectData Model, Matrix4x4 Transform, SceneOrientedBounds Bounds,
    WorldAssetUnavailableReason UnavailableReason = WorldAssetUnavailableReason.None)
{
    public long InstanceId { get; init; }
    public Quaternion Rotation { get; init; } = Quaternion.Identity;
    public Vector3 Scale { get; init; } = Vector3.One;
    public bool HasBounds { get; init; } = true;
    public string? GeometryPath { get; init; }

    public string Path => ObjectSnapshotUtility.GetDeclaredRootResourcePath(Model);
    public ObjectKind Kind => Model.Kind;

    public SceneTransform CreatePlacementTransform()
        => new() { Position = Transform.Translation, RotationDegrees = SceneTransformMath.ToRotationDegrees(Rotation), Scale = Scale };
}

internal interface IWorldAssetSource
{
    /// <summary> captures ray candidates, one selected instance, or all assets when neither is supplied </summary>
    bool Capture(ScenePointerRay? ray, List<WorldAsset> assets, long selectedInstance = 0);

    void Clear();
}

internal sealed unsafe class WorldAssetSource(IObjectSceneState scene) : IWorldAssetSource
{
    private sealed record CachedAsset(nint ResourceHandle, string ResourcePath, WorldAsset Asset, AxisAlignedBounds LocalBounds, ObjectSnapshot? Owner = null);
    private Dictionary<nint, CachedAsset> _assets = [];
    private Dictionary<nint, CachedAsset> _captured = [];
    private nint _activeLayout;
    private long _nextInstanceId;
    private readonly HashSet<nint> _excludedGraphics = [];
    private readonly HashSet<nint> _entryGraphics = [];
    private readonly Dictionary<nint, ObjectSnapshot> _localGraphics = [];
    private readonly Dictionary<nint, string> _resourcePaths = [];

    public bool Capture(ScenePointerRay? ray, List<WorldAsset> assets, long selectedInstance = 0)
    {
        if (scene.GetRefreshState().IsZoning)
        {
            Clear();
            return false;
        }

        World* world = World.Instance();
        LayoutWorld* layouts = LayoutWorld.Instance();

        if (world == null || layouts == null || layouts->ActiveLayout == null || layouts->ActiveLayout->InitState != 7)
        {
            Clear();
            return false;
        }

        if (_activeLayout != (nint)layouts->ActiveLayout)
        {
            Clear();
            _activeLayout = (nint)layouts->ActiveLayout;
        }

        _captured.Clear();
        _excludedGraphics.Clear();
        _entryGraphics.Clear();
        _localGraphics.Clear();
        _resourcePaths.Clear();
        IReadOnlyList<ObjectSceneEntry> entries = scene.GetEntriesSnapshot();
        for (int index = 0; index < entries.Count; ++index)
        {
            ObjectSceneEntry entry = entries[index];
            if (entry.Source.IsRuntimeOnly)
            {
                entry.Runtime.AppendGraphicsObjects(_excludedGraphics);
                continue;
            }

            _entryGraphics.Clear();
            entry.Runtime.AppendGraphicsObjects(_entryGraphics);
            foreach (nint address in _entryGraphics)
            {
                _localGraphics[address] = entry.Snapshot;
            }
        }

        CaptureChildren((SceneObject*)world, ray, selectedInstance, assets);
        if (ray is null && selectedInstance != 0)
        {
            foreach ((nint address, CachedAsset captured) in _captured)
            {
                _assets[address] = captured;
            }
        }
        else
        {
            (_assets, _captured) = (_captured, _assets);
        }

        _captured.Clear();

        return true;
    }

    public void Clear()
    {
        _assets.Clear();
        _captured.Clear();
        _activeLayout = 0;
        _excludedGraphics.Clear();
        _entryGraphics.Clear();
        _localGraphics.Clear();
        _resourcePaths.Clear();
    }

    private bool CaptureChildren(SceneObject* parent, ScenePointerRay? ray, long selectedInstance, List<WorldAsset> assets)
    {
        foreach (SceneObject* child in parent->ChildObjects)
        {
            if (_excludedGraphics.Contains((nint)child))
            {
                continue;
            }

            _assets.TryGetValue((nint)child, out CachedAsset? cached);
            bool selected = selectedInstance != 0 && cached?.Asset.InstanceId == selectedInstance;

            WorldAsset? asset = null;
            if (ray.HasValue || selectedInstance == 0 || selected)
            {
                ScenePointerRay? captureRay = selected ? null : ray;
                switch (child->GetObjectType())
                {
                    case ObjectType.BgObject:
                        TryCaptureBgObject((BgObject*)child, captureRay, out asset);
                        break;
                    case ObjectType.VfxObject:
                        TryCaptureVfx((VfxObject*)child, captureRay, out asset);
                        break;
                }
            }

            if (asset is not null)
            {
                assets.Add(asset);
            }
            else if (cached is not null && (ray.HasValue || selectedInstance == 0))
            {
                _captured[(nint)child] = cached;
            }

            if (selected && ray is null || CaptureChildren(child, ray, selectedInstance, assets))
            {
                return true;
            }
        }

        return false;
    }

    internal bool TryCaptureBgObject(BgObject* model, ScenePointerRay? ray, out WorldAsset? snapshot)
    {
        snapshot = null;

        if (!BgObjectSceneInterop.IsModelLoaded(model) || !model->IsVisible
            || model->ModelResourceHandle->AxisAlignedBounds == null)
        {
            return false;
        }

        var localBounds = *model->ModelResourceHandle->AxisAlignedBounds;

        if (!ObjectShapeMath.IsValidBounds(localBounds))
        {
            return false;
        }

        Vector3 center = (localBounds.Min + localBounds.Max) * 0.5f;
        Vector3 halfExtents = (localBounds.Max - localBounds.Min) * 0.5f;
        Quaternion rotation = model->Rotation;
        Vector3 scale = model->Scale;
        if (ray is { } broadRay)
        {
            Vector3 worldCenter = Vector3.Transform(center * scale, rotation) + (Vector3)model->Position;
            float vectorLengthSquared = rotation.X * rotation.X + rotation.Y * rotation.Y + rotation.Z * rotation.Z;
            float rotationScaleSquared = MathF.Max(1f, 1f + 4f * vectorLengthSquared * (rotation.LengthSquared() - 1f));
            float radius = MathF.Sqrt((halfExtents * scale).LengthSquared() * rotationScaleSquared);
            if (!SceneRaycastMath.MayIntersectSphere(broadRay, worldCenter, radius))
            {
                return false;
            }
        }

        _assets.TryGetValue((nint)model, out CachedAsset? cached);
        nint resource = (nint)model->ModelResourceHandle;
        string path = ReadResourcePath(&model->ModelResourceHandle->ResourceHandle, cached);
        _localGraphics.TryGetValue((nint)model, out ObjectSnapshot? owner);
        if (ObjectResourcePathUtility.IsOwnedResourcePath(path) && owner is null)
        {
            return false;
        }

        Vector4 dye = model->StainBuffer == null ? Vector4.One : model->StainBuffer->LinearFloatColor;
        if (model->StainBuffer != null)
        {
            dye.W = model->StainBuffer->SrgbByteColor.A / 255f;
        }

        float transparency = model->GetTransparency();
        bool covered = model->IsCoveredFromRain;
        WorldAssetUnavailableReason animation = model->LoadedAnimationData != null && model->LoadedAnimationData->RenderSkeleton != null
            ? WorldAssetUnavailableReason.Animated : WorldAssetUnavailableReason.None;
        bool sameResource = cached is not null && cached.Asset.Model is BgObjectModel && cached.ResourceHandle == resource
            && cached.Owner?.Id == owner?.Id
            && (owner?.Model is not BgObjectModel owned || string.Equals(cached.Asset.Path, owned.ModelPath, StringComparison.Ordinal))
            && string.Equals(cached.ResourcePath, path, StringComparison.Ordinal)
            && (cached.Asset.UnavailableReason == WorldAssetUnavailableReason.Animated) == (animation == WorldAssetUnavailableReason.Animated);
        bool sameGeometry = sameResource && cached!.Asset.Transform.Translation == (Vector3)model->Position
            && cached.Asset.Rotation == rotation && cached.Asset.Scale == scale
            && (Vector3)cached.LocalBounds.Min == (Vector3)localBounds.Min
            && (Vector3)cached.LocalBounds.Max == (Vector3)localBounds.Max;
        Matrix4x4 transform;
        SceneOrientedBounds bounds;

        if (sameGeometry)
        {
            transform = cached!.Asset.Transform;
            bounds = cached.Asset.Bounds;
        }
        else
        {
            transform = Matrix4x4.CreateScale(scale)
                * Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(model->Position);
            bounds = new(Matrix4x4.CreateTranslation(center) * transform, halfExtents);

            if (!NumericsUtility.IsFinite(transform) || !Matrix4x4.Invert(transform, out Matrix4x4 inverse)
                || !NumericsUtility.IsFinite(inverse))
            {
                return false;
            }
        }

        if (ray is { } pointer && !SceneBoundsRaycaster.TryIntersectOrientedBoundsNormalized(pointer, bounds, out _))
        {
            return false;
        }

        if (sameResource)
        {
            BgObjectModel previous = (BgObjectModel)cached!.Asset.Model;
            BgObjectModel data = ReferenceEquals(cached.Owner?.Model, owner?.Model)
                ? previous : owner?.Model as BgObjectModel ?? previous;

            if (data.DyeColor != dye || data.Transparency.CompareTo(transparency) != 0 || data.IsCoveredFromRain != covered)
            {
                data = data with { DyeColor = dye, Transparency = transparency, IsCoveredFromRain = covered };
            }

            snapshot = sameGeometry && data == previous ? cached.Asset : cached.Asset with
            {
                Transform = transform, Bounds = bounds, Rotation = rotation, Scale = scale, Model = data,
            };
        }
        else
        {
            string assetPath = ResolveAssetPath(owner, path, ObjectKind.BgObject);
            WorldAssetUnavailableReason reason = ObjectAssetPathRules.IsCatalogModelPath(assetPath)
                ? WorldAssetUnavailableReason.None : WorldAssetUnavailableReason.UnresolvedResource;
            if (animation != WorldAssetUnavailableReason.None)
            {
                reason = animation;
            }

            BgObjectModel data = owner?.Model as BgObjectModel ?? new BgObjectModel { ModelPath = assetPath.Length > 0 ? assetPath : path };
            snapshot = new WorldAsset(data with
            {
                DyeColor = dye, Transparency = transparency, IsCoveredFromRain = covered,
            }, transform, bounds, reason)
            {
                InstanceId = ++_nextInstanceId, Rotation = rotation, Scale = scale,
                GeometryPath = ObjectResourcePathUtility.NormalizeTrackedPath(path),
            };
        }

        _captured[(nint)model] = cached is not null && ReferenceEquals(snapshot, cached.Asset)
            && ReferenceEquals(cached.Owner?.Model, owner?.Model) ? cached : new(resource, path, snapshot, localBounds, owner);

        return true;
    }

    internal bool TryCaptureVfx(VfxObject* vfx, ScenePointerRay? ray, out WorldAsset? snapshot)
    {
        snapshot = null;
        ApricotResourceHandle* handle = ObjectSceneInterop.GetVfxResourceHandle(vfx);
        if (!ObjectSceneInterop.IsDrawObjectLoaded((DrawObject*)vfx) || !vfx->IsVisible
            || handle == null || handle->ReadState != 2 || handle->LoadState != 7)
        {
            return false;
        }

        bool hasBounds = ObjectSceneInterop.TryGetDrawObjectOrientedBounds((DrawObject*)vfx, out var nativeBounds);
        SceneOrientedBounds bounds = hasBounds ? new(nativeBounds.Transform, nativeBounds.HalfExtents) : default;
        if (ray is { } pointer && (!hasBounds || !SceneBoundsRaycaster.TryIntersectOrientedBoundsNormalized(pointer, bounds, out _)))
        {
            return false;
        }

        Matrix4x4 transform = Matrix4x4.CreateScale(vfx->Scale) * Matrix4x4.CreateFromQuaternion(vfx->Rotation)
            * Matrix4x4.CreateTranslation(vfx->Position);
        if (!NumericsUtility.IsFinite(transform) || !NumericsUtility.IsFinite((Vector4)vfx->Color) || !float.IsFinite(vfx->Speed))
        {
            return false;
        }

        _assets.TryGetValue((nint)vfx, out CachedAsset? cached);
        string path = ReadResourcePath((ResourceHandle*)handle, cached);
        _localGraphics.TryGetValue((nint)vfx, out ObjectSnapshot? owner);
        if (ObjectResourcePathUtility.IsOwnedResourcePath(path) && owner is null)
        {
            return false;
        }

        bool sameResource = cached?.Asset.Model is VfxModel && cached.ResourceHandle == (nint)handle
            && cached.Owner?.Id == owner?.Id
            && (owner?.Model is not VfxModel owned || string.Equals(cached.Asset.Path, owned.VfxPath, StringComparison.Ordinal))
            && string.Equals(cached.ResourcePath, path, StringComparison.Ordinal);
        string assetPath = sameResource ? cached!.Asset.Path : ResolveAssetPath(owner, path, ObjectKind.Vfx);
        VfxModel data;
        if (sameResource && ReferenceEquals(cached!.Owner?.Model, owner?.Model))
        {
            data = (VfxModel)cached.Asset.Model;
        }
        else
        {
            data = owner?.Model as VfxModel ?? new VfxModel { VfxPath = assetPath };
        }

        if (data.Color != (Vector4)vfx->Color || data.Speed.CompareTo(vfx->Speed) != 0)
        {
            data = data with { Color = vfx->Color, Speed = vfx->Speed };
        }

        if (sameResource && cached!.Asset.Transform == transform && cached.Asset.Bounds == bounds
            && cached.Asset.HasBounds == hasBounds && cached.Asset.Model == data)
        {
            snapshot = cached.Asset;
        }
        else
        {
            snapshot = new WorldAsset(data, transform, bounds,
                GameAssetPathRules.IsFileKind(assetPath, GameAssetFileKind.Avfx)
                    ? WorldAssetUnavailableReason.None : WorldAssetUnavailableReason.UnresolvedResource)
            {
                InstanceId = sameResource ? cached!.Asset.InstanceId : ++_nextInstanceId,
                Rotation = vfx->Rotation, Scale = vfx->Scale, HasBounds = hasBounds,
            };
        }

        _captured[(nint)vfx] = cached is not null && ReferenceEquals(snapshot, cached.Asset) && ReferenceEquals(cached.Owner?.Model, owner?.Model)
            ? cached : new((nint)handle, path, snapshot, default, owner);
        return true;
    }

    private string ReadResourcePath(ResourceHandle* handle, CachedAsset? cached)
    {
        if (!_resourcePaths.TryGetValue((nint)handle, out string? path))
        {
            ObjectResourcePathEncoding.TryReadHandlePath(handle, out path, cached?.ResourcePath);
            _resourcePaths.Add((nint)handle, path);
        }

        return path;
    }

    private static string ResolveAssetPath(ObjectSnapshot? owner, string path, ObjectKind kind)
    {
        if (owner is not null)
        {
            if (owner.Kind == kind)
            {
                return ObjectSnapshotUtility.GetRootResourcePath(owner);
            }

            path = ObjectScopedResourcePathUtility.Strip(path);
            if (ObjectMemoryResourcePathUtility.TryParse(path, out ObjectMemoryResourcePath memory))
            {
                path = memory.GamePath;
            }
        }

        return ObjectResolvedPath.FromGamePath(path).Path;
    }
}