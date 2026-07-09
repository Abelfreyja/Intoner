using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Group;
using Intoner.Objects.Assets;
using Intoner.Objects.Interop;
using Intoner.Objects.Models;
using Intoner.Objects.UI;
using Intoner.Objects.Utils;
using System.Numerics;

namespace Intoner.Objects.Runtime;

/// <summary>
/// Describes supported object kinds and sanitizes object snapshots.
/// </summary>
internal interface IObjectKindService
{
    /// <summary>
    /// Gets the supported object kinds and their current create availability.
    /// </summary>
    /// <returns>The current object kind metadata.</returns>
    IReadOnlyList<ObjectKindInfo> GetKindInfos();

    /// <summary>
    /// Gets the display name for the given object kind.
    /// </summary>
    /// <param name="kind">The object kind to resolve.</param>
    /// <returns>The display name for the kind.</returns>
    string GetDisplayName(ObjectKind kind);

    /// <summary>
    /// Checks whether the given object kind can currently be created.
    /// </summary>
    /// <param name="kind">The object kind to check.</param>
    /// <returns>True when the kind can currently be created.</returns>
    bool CanCreate(ObjectKind kind);

    /// <summary>
    /// Creates a default snapshot for the given object kind.
    /// </summary>
    /// <param name="kind">The object kind to create.</param>
    /// <param name="transform">The initial object transform.</param>
    /// <param name="name">The initial object name.</param>
    /// <returns>The default snapshot for the requested kind.</returns>
    ObjectSnapshot CreateDefaultSnapshot(ObjectKind kind, ObjectTransform transform, string name);

    /// <summary>
    /// Sanitizes one snapshot according to its object kind.
    /// </summary>
    /// <param name="snapshot">The snapshot to sanitize.</param>
    /// <param name="sanitizedSnapshot">The sanitized snapshot when successful.</param>
    /// <returns>true when the snapshot kind is supported.</returns>
    bool TrySanitizeSnapshot(ObjectSnapshot snapshot, out ObjectSnapshot sanitizedSnapshot);
}

internal sealed class ObjectKindService : IObjectKindService
{
    private readonly ObjectNativeBindings _nativeBindings;

    public ObjectKindService(ObjectNativeBindings nativeBindings)
    {
        _nativeBindings = nativeBindings;
    }

    public IReadOnlyList<ObjectKindInfo> GetKindInfos()
        => Enum.GetValues<ObjectKind>()
            .Order()
            .Select(kind => new ObjectKindInfo
            {
                Kind = kind,
                DisplayName = GetDisplayName(kind),
                CanCreate = CanCreate(kind),
            })
            .ToList();

    public string GetDisplayName(ObjectKind kind)
        => kind switch
        {
            ObjectKind.BgObject => "BgObject",
            ObjectKind.Furniture => "Furniture",
            ObjectKind.Vfx => "VFX",
            ObjectKind.Light => "Light",
            _ => kind.ToString(),
        };

    public bool CanCreate(ObjectKind kind)
        => kind switch
        {
            ObjectKind.BgObject => true,
            ObjectKind.Furniture => _nativeBindings.Furniture.IsAvailable,
            ObjectKind.Vfx => true,
            ObjectKind.Light => true,
            _ => false,
        };

    public ObjectSnapshot CreateDefaultSnapshot(ObjectKind kind, ObjectTransform transform, string name)
        => kind switch
        {
            ObjectKind.BgObject => CreateSnapshot(kind, transform, name, new BgObjectModel()),
            ObjectKind.Furniture => CreateSnapshot(kind, transform, name, new FurnitureModel()),
            ObjectKind.Vfx => CreateSnapshot(kind, transform, name, new VfxModel()),
            ObjectKind.Light => CreateSnapshot(kind, transform, name, new LightModel()),
            _ => throw new InvalidOperationException($"unsupported object kind {kind}"),
        };

    public bool TrySanitizeSnapshot(ObjectSnapshot snapshot, out ObjectSnapshot sanitizedSnapshot)
    {
        sanitizedSnapshot = snapshot.Kind switch
        {
            ObjectKind.BgObject => SanitizeBgObjectSnapshot(snapshot),
            ObjectKind.Furniture => SanitizeFurnitureSnapshot(snapshot),
            ObjectKind.Vfx => SanitizeVfxSnapshot(snapshot),
            ObjectKind.Light => SanitizeLightSnapshot(snapshot),
            _ => default!,
        };

        if (snapshot.Kind is not (ObjectKind.BgObject or ObjectKind.Furniture or ObjectKind.Vfx or ObjectKind.Light))
        {
            return false;
        }

        sanitizedSnapshot = sanitizedSnapshot with
        {
            CollectionId = SanitizeCollectionId(snapshot.CollectionId),
        };
        return true;
    }

    private static ObjectSnapshot CreateSnapshot(ObjectKind kind, ObjectTransform transform, string name, ObjectData model)
        => new()
        {
            Id = Guid.NewGuid(),
            Name = SanitizeName(name, kind.ToString()),
            Kind = kind,
            Transform = transform with { Scale = Vector3.One },
            Model = model,
            CreatedAtUtc = DateTime.UtcNow,
        };

    private static string SanitizeName(string name, string fallbackName)
        => ObjectStringUtility.TrimOrFallback(name, fallbackName);

    private static string SanitizeCollectionId(string collectionId)
        => ObjectCollectionKeyUtility.NormalizeCollectionId(collectionId);

    private static Vector3 ClampScale(Vector3 scale, float minScale = 0.01f)
        => new(
            MathF.Max(minScale, scale.X),
            MathF.Max(minScale, scale.Y),
            MathF.Max(minScale, scale.Z));

    private static TModel RequireModel<TModel>(ObjectSnapshot snapshot, ObjectKind expectedKind)
        where TModel : ObjectData
    {
        if (snapshot.Kind != expectedKind)
        {
            throw new InvalidOperationException($"expected kind {expectedKind} but received {snapshot.Kind}");
        }

        if (snapshot.Model is TModel typedModel)
        {
            return typedModel;
        }

        throw new InvalidOperationException($"expected model {typeof(TModel).Name} but received {snapshot.Model.GetType().Name}");
    }

    private static ObjectSnapshot SanitizeBgObjectSnapshot(ObjectSnapshot snapshot)
    {
        var bgObjectModel = RequireModel<BgObjectModel>(snapshot, ObjectKind.BgObject);
        return snapshot with
        {
            Name = SanitizeName(snapshot.Name, "BgObject"),
            FolderPath = ObjectFolderUtility.SanitizeFolderPath(snapshot.FolderPath),
            Transform = snapshot.Transform with
            {
                Scale = ClampScale(snapshot.Transform.Scale),
            },
            Model = bgObjectModel with
            {
                ModelPath = GameAssetPathRules.NormalizeGamePath(bgObjectModel.ModelPath),
                Transparency = Math.Clamp(bgObjectModel.Transparency, 0f, 1f),
                DyeColor = ObjectColorUtility.ClampNormalizedColor(bgObjectModel.DyeColor),
            },
        };
    }

    private static ObjectSnapshot SanitizeFurnitureSnapshot(ObjectSnapshot snapshot)
    {
        var furnitureModel = RequireModel<FurnitureModel>(snapshot, ObjectKind.Furniture);
        return snapshot with
        {
            Name = SanitizeName(snapshot.Name, "Furniture"),
            FolderPath = ObjectFolderUtility.SanitizeFolderPath(snapshot.FolderPath),
            Transform = snapshot.Transform with
            {
                Scale = ClampScale(snapshot.Transform.Scale),
            },
            Model = furnitureModel with
            {
                SharedGroupPath = GameAssetPathRules.NormalizeGamePath(furnitureModel.SharedGroupPath),
                Color = furnitureModel.Color with
                {
                    StainId = Math.Min(furnitureModel.Color.StainId, (byte)(SharedGroupLayoutInstance.ObjectStainCount - 1)),
                    CustomColor = ObjectColorUtility.ClampOpaqueNormalizedColor(furnitureModel.Color.CustomColor),
                },
                Transparency = Math.Clamp(furnitureModel.Transparency, 0f, 1f),
                OutlineColor = Enum.IsDefined(furnitureModel.OutlineColor)
                    ? furnitureModel.OutlineColor
                    : ObjectOutlineColor.None,
            },
        };
    }

    private static ObjectSnapshot SanitizeVfxSnapshot(ObjectSnapshot snapshot)
    {
        var vfxModel = RequireModel<VfxModel>(snapshot, ObjectKind.Vfx);
        return snapshot with
        {
            Name = SanitizeName(snapshot.Name, "VFX"),
            FolderPath = ObjectFolderUtility.SanitizeFolderPath(snapshot.FolderPath),
            Transform = snapshot.Transform with
            {
                Scale = ClampScale(snapshot.Transform.Scale),
            },
            Model = vfxModel with
            {
                VfxPath = GameAssetPathRules.NormalizeGamePath(vfxModel.VfxPath),
                Color = ObjectColorUtility.ClampNormalizedColor(vfxModel.Color),
                LoopIntervalSeconds = VfxModel.ClampLoopIntervalSeconds(vfxModel.LoopIntervalSeconds),
            },
        };
    }

    private static ObjectSnapshot SanitizeLightSnapshot(ObjectSnapshot snapshot)
    {
        var lightModel = RequireModel<LightModel>(snapshot, ObjectKind.Light);
        var shape = lightModel.Shape;
        var shadow = lightModel.Shadow;

        return snapshot with
        {
            Name = SanitizeName(snapshot.Name, "Light"),
            FolderPath = ObjectFolderUtility.SanitizeFolderPath(snapshot.FolderPath),
            Model = lightModel with
            {
                Color = new Vector3(
                    Math.Clamp(lightModel.Color.X, 0f, 50f),
                    Math.Clamp(lightModel.Color.Y, 0f, 50f),
                    Math.Clamp(lightModel.Color.Z, 0f, 50f)),
                Intensity = Math.Clamp(lightModel.Intensity, 0f, 100f),
                Shape = shape with
                {
                    Range = Math.Clamp(shape.Range, 0.01f, 900f),
                    Falloff = Math.Clamp(shape.Falloff, 0f, 1000f),
                    LightAngle = Math.Clamp(shape.LightAngle, 0f, 180f),
                    FalloffAngle = Math.Clamp(shape.FalloffAngle, 0f, 180f),
                    AngleDegrees = new Vector2(
                        Math.Clamp(shape.AngleDegrees.X, -90f, 90f),
                        Math.Clamp(shape.AngleDegrees.Y, -90f, 90f)),
                },
                Shadow = shadow with
                {
                    CharacterShadowRange = Math.Clamp(shadow.CharacterShadowRange, 0f, 1000f),
                    ShadowPlaneNear = Math.Clamp(shadow.ShadowPlaneNear, 0.001f, 100f),
                    ShadowPlaneFar = Math.Clamp(shadow.ShadowPlaneFar, 0.01f, 1000f),
                },
            },
        };
    }

}

