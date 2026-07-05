using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Group;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Node;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using Intoner.Objects.Models;
using Intoner.Objects.Utils;
using System.Numerics;
using System.Runtime.InteropServices;
using AxisAlignedBounds = FFXIVClientStructs.FFXIV.Common.Math.AxisAlignedBounds;
using OrientedBounds = FFXIVClientStructs.FFXIV.Common.Math.OrientedBounds;
using SceneObject = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Object;

namespace Intoner.Objects.Interop;

internal readonly record struct SharedGroupChildState(nint First, nint Last);

/// <summary>
/// Provides native layout instance helpers shared by the object runtime.
/// </summary>
internal static unsafe class ObjectLayoutInterop
{
    private static readonly InstanceType[] ColliderOwnerTypes =
    [
        InstanceType.BgPart,
        InstanceType.SharedGroup,
        InstanceType.CollisionBox,
        InstanceType.SphereCastRange,
        InstanceType.IndoorObject,
        InstanceType.OutdoorObject,
        InstanceType.ColliderLayer7,
        InstanceType.ColliderLayer8,
        InstanceType.ColliderLayer9,
        InstanceType.ColliderLayer10,
    ];

    private const int SharedGroupReadyVtableSlot = 22;
    private const int LayoutGetWorldBoundsVtableSlot = 73;
    private const int LayoutGetTransformExtentsVtableSlot = 74;
    private const int SharedGroupPlacementCategoryOffset = 0x00;
    private const int SharedGroupPlacementFlagsOffset = 0xC4;
    private const byte SharedGroupTabletopCategory = 0x0E;
    private const byte SharedGroupWallCategory = 0x0F;
    private const ushort SharedGroupTabletopPlacementFlag = 0x0400;
    private const ushort SharedGroupWallPlacementFlag = 0x0800;

    [StructLayout(LayoutKind.Explicit, Size = 0x50)]
    private struct LayoutTransformExtents
    {
        [FieldOffset(0x30)] public float CenterX;
        [FieldOffset(0x34)] public float CenterY;
        [FieldOffset(0x38)] public float CenterZ;
        [FieldOffset(0x40)] public float ExtentX;
        [FieldOffset(0x44)] public float ExtentY;
        [FieldOffset(0x48)] public float ExtentZ;
    }

    /// <summary>
    /// Creates a native layout transform from one object transform snapshot.
    /// </summary>
    /// <param name="transform">The object transform to convert.</param>
    /// <returns>The native layout transform.</returns>
    public static Transform CreateTransform(ObjectTransform transform)
        => new()
        {
            Translation = transform.Position,
            Rotation = CreateRotation(transform.RotationDegrees),
            Scale = transform.Scale,
        };

    /// <summary>
    /// Applies one object transform to a layout instance.
    /// </summary>
    /// <param name="instance">The layout instance to update.</param>
    /// <param name="transform">The object transform to apply.</param>
    public static void ApplyTransform(ILayoutInstance* instance, ObjectTransform transform)
    {
        var layoutTransform = CreateTransform(transform);
        instance->SetTransformImpl(&layoutTransform);
    }

    /// <summary>
    /// Applies one object transform to a shared group layout instance and refreshes child transforms.
    /// </summary>
    /// <param name="instance">The shared group layout instance to update.</param>
    /// <param name="transform">The object transform to apply.</param>
    public static void ApplyTransform(SharedGroupLayoutInstance* instance, ObjectTransform transform)
    {
        var layoutTransform = CreateTransform(transform);
        instance->SetTransformImpl(&layoutTransform);
        instance->Instances.ApplyTransforms();
    }

    /// <summary>
    /// Checks whether a sgb has finished native child setup.
    /// </summary>
    /// <param name="instance">the sgb layout instance to inspect.</param>
    /// <returns>true when the native sgb reports ready.</returns>
    public static bool IsSharedGroupReady(SharedGroupLayoutInstance* instance)
    {
        if (instance == null)
        {
            return false;
        }

        var vtable = *(nint**)instance;
        if (vtable == null)
        {
            return false;
        }

        var readyFunction = (delegate* unmanaged<SharedGroupLayoutInstance*, byte>)vtable[SharedGroupReadyVtableSlot];
        return readyFunction != null && readyFunction(instance) != 0;
    }

    /// <summary>
    /// Refreshes visual sgb children after native child setup is ready.
    /// </summary>
    /// <param name="instance">the sgb layout instance to update.</param>
    /// <returns>true when refresh was applied.</returns>
    public static bool TryRefreshVisualSharedGroupState(SharedGroupLayoutInstance* instance)
    {
        if (!IsSharedGroupReady(instance))
        {
            return false;
        }

        instance->Instances.ApplyTransforms();
        ((ILayoutInstance*)instance)->SetColliderActive(false);
        instance->Instances.SetCollidersActive(false);
        RefreshNestedVisualSharedGroupState(&instance->Instances);
        return true;
    }

    /// <summary>
    /// Gets the current visible child vector state for a sgb.
    /// </summary>
    /// <param name="instance">the sgb layout instance to inspect.</param>
    /// <returns>the current child vector bounds.</returns>
    public static SharedGroupChildState GetSharedGroupChildState(SharedGroupLayoutInstance* instance)
        => instance == null
            ? default
            : new SharedGroupChildState(
                (nint)instance->Instances.Instances.First,
                (nint)instance->Instances.Instances.Last);

    /// <summary>
    /// Checks whether a visual sgb root or child collider is currently active.
    /// </summary>
    /// <param name="instance">the sgb layout instance to inspect.</param>
    /// <returns>true when any root or child collider is active.</returns>
    public static bool HasActiveVisualSharedGroupColliders(SharedGroupLayoutInstance* instance)
        => instance != null
            && (((ILayoutInstance*)instance)->IsColliderActive()
                || HasActiveChildColliders(&instance->Instances));

    /// <summary>
    /// Checks whether one collider belongs to a shared group or any of its children.
    /// </summary>
    /// <param name="sharedGroupAddress">The shared group layout instance address.</param>
    /// <param name="colliderAddress">The native collider address to check.</param>
    /// <returns>true when the collider belongs to the shared group tree.</returns>
    public static bool SharedGroupContainsCollider(nint sharedGroupAddress, nint colliderAddress)
    {
        if (sharedGroupAddress == 0 || colliderAddress == 0)
        {
            return false;
        }

        ILayoutInstance* root = (ILayoutInstance*)sharedGroupAddress;
        Collider* collider = (Collider*)colliderAddress;
        return InstanceTreeContainsCollider(root, collider)
            || (TryResolveColliderLayoutInstance(collider, out ILayoutInstance* owner)
                && InstanceTreeContainsInstance(root, owner));
    }

    /// <summary>
    /// Applies the active and collider state to one layout instance.
    /// </summary>
    /// <param name="instance">The layout instance to update.</param>
    /// <param name="visible">Whether the instance should be active.</param>
    /// <param name="collidersActive">Whether layout colliders should stay active.</param>
    public static void ApplyVisibilityState(ILayoutInstance* instance, bool visible, bool collidersActive = false)
    {
        instance->SetActive(visible);
        instance->SetColliderActive(collidersActive);
    }

    /// <summary>
    /// Tries to resolve one layout instance world transform.
    /// </summary>
    /// <param name="instance">The layout instance to inspect.</param>
    /// <param name="worldTransform">The resolved world transform when available.</param>
    /// <returns>true when the layout instance reported a transform.</returns>
    public static bool TryGetWorldTransform(ILayoutInstance* instance, out Matrix4x4 worldTransform)
    {
        worldTransform = default;
        if (instance == null)
        {
            return false;
        }

        Transform transform = default;
        if (instance->GetTransform(&transform) == null)
        {
            return false;
        }

        worldTransform = transform.Compose();
        return true;
    }

    /// <summary>
    /// Tries to resolve world bounds for one layout instance.
    /// </summary>
    /// <param name="instance">The layout instance to inspect.</param>
    /// <param name="bounds">The resolved bounds when available.</param>
    /// <returns>true when the layout instance reported bounds.</returns>
    public static bool TryGetBounds(ILayoutInstance* instance, out AxisAlignedBounds bounds)
    {
        bounds = default;
        if (instance == null)
        {
            return false;
        }

        var vtable = *(nint**)instance;
        if (vtable == null)
        {
            return false;
        }

        var function = (delegate* unmanaged<ILayoutInstance*, AxisAlignedBounds*, AxisAlignedBounds*>)vtable[LayoutGetWorldBoundsVtableSlot];
        if (function == null)
        {
            return false;
        }

        fixed (AxisAlignedBounds* boundsPtr = &bounds)
        {
            return function(instance, boundsPtr) != null;
        }
    }

    /// <summary>
    /// Tries to resolve oriented local bounds for one shared group layout instance.
    /// </summary>
    /// <param name="instance">The shared group layout instance to inspect.</param>
    /// <param name="bounds">The resolved oriented bounds when available.</param>
    /// <returns>true when bounds were resolved from the shared group children.</returns>
    public static bool TryGetSharedGroupOrientedBounds(SharedGroupLayoutInstance* instance, out OrientedBounds bounds)
    {
        bounds = default;
        if (instance == null)
        {
            return false;
        }

        var rootTransform = ObjectShapeMath.CreateRigidTransform(instance->Transform.Translation, instance->Transform.Rotation);
        if (!Matrix4x4.Invert(rootTransform, out var inverseRootTransform))
        {
            return false;
        }

        var hasLocalBounds = false;
        var localMin = Vector3.Zero;
        var localMax = Vector3.Zero;
        AccumulateSharedGroupLocalBounds(&instance->Instances, inverseRootTransform, ref hasLocalBounds, ref localMin, ref localMax);
        if (!hasLocalBounds)
        {
            return false;
        }

        var localCenter = (localMin + localMax) * 0.5f;
        bounds = new OrientedBounds
        {
            Transform = ObjectShapeMath.CreateRigidTransform(Vector3.Transform(localCenter, rootTransform), instance->Transform.Rotation),
            HalfExtents = (localMax - localMin) * 0.5f,
        };
        return true;
    }

    /// <summary>
    /// Tries to resolve the native floor placement clearance for a sgb.
    /// </summary>
    /// <param name="instance">the sgb layout instance to inspect.</param>
    /// <param name="clearance">the resolved placement clearance when available.</param>
    /// <returns>true when placement clearance was resolved.</returns>
    public static bool TryGetSharedGroupPlacementClearance(SharedGroupLayoutInstance* instance, out ObjectPlacementClearance clearance)
    {
        clearance = default;
        if (instance == null)
        {
            return false;
        }

        return TryGetPlacementClearance(ResolveSharedGroupPlacementInstance(instance), out clearance);
    }

    /// <summary>
    /// gets native placement surface support for one sgb
    /// </summary>
    /// <param name="instance">the sgb layout instance to inspect</param>
    /// <returns>the placement surfaces exposed by the shared group</returns>
    public static ObjectPlacementSurfaceSupport GetSharedGroupPlacementSurfaceSupport(SharedGroupLayoutInstance* instance)
    {
        if (instance == null || instance->StainInfo == null)
        {
            return ObjectPlacementSurfaceSupport.None;
        }

        byte* stainInfo = (byte*)instance->StainInfo;
        byte category = stainInfo[SharedGroupPlacementCategoryOffset];
        ushort placementFlags = *(ushort*)(stainInfo + SharedGroupPlacementFlagsOffset);
        ObjectPlacementSurfaceSupport support = ObjectPlacementSurfaceSupport.None;

        // native housing placement reads category +0 and the high flags byte at +0xc5
        if (category == SharedGroupTabletopCategory
            || (placementFlags & SharedGroupTabletopPlacementFlag) != 0)
        {
            support |= ObjectPlacementSurfaceSupport.Tabletop;
        }

        if (category == SharedGroupWallCategory
            || (placementFlags & SharedGroupWallPlacementFlag) != 0)
        {
            support |= ObjectPlacementSurfaceSupport.Wall;
        }

        return support;
    }

    private static Quaternion CreateRotation(Vector3 rotationDegrees)
        => ObjectTransformMath.CreateRotationQuaternion(rotationDegrees);

    private static void RefreshNestedVisualSharedGroupState(ChildNodeContainer* container)
    {
        foreach (var child in container->Instances)
        {
            var node = child.Value;
            if (node == null || node->Instance == null || node->Instance->Id.Type != InstanceType.SharedGroup)
            {
                continue;
            }

            _ = TryRefreshVisualSharedGroupState((SharedGroupLayoutInstance*)node->Instance);
        }
    }

    private static bool TryFindImmediateChild(
        ChildNodeContainer* container,
        InstanceType type,
        out ILayoutInstance* instance)
    {
        instance = null;
        foreach (var child in container->Instances)
        {
            var node = child.Value;
            if (node == null || node->Instance == null || node->Instance->Id.Type != type)
            {
                continue;
            }

            instance = node->Instance;
            return true;
        }

        return false;
    }

    private static ILayoutInstance* ResolveSharedGroupPlacementInstance(SharedGroupLayoutInstance* instance)
    {
        if (TryFindImmediateChild(&instance->Instances, InstanceType.SphereCastRange, out ILayoutInstance* placementInstance)
            || TryFindImmediateChild(&instance->Instances, InstanceType.BgPart, out placementInstance))
        {
            return placementInstance;
        }

        return (ILayoutInstance*)instance;
    }

    private static bool TryGetPlacementClearance(ILayoutInstance* instance, out ObjectPlacementClearance clearance)
    {
        clearance = default;
        if (!TryGetLayoutTransformExtents(instance, out LayoutTransformExtents extents))
        {
            return false;
        }

        return ObjectPlacementClearance.TryCreate(MathF.Min(extents.ExtentX, extents.ExtentZ), out clearance);
    }

    private static bool TryGetLayoutTransformExtents(ILayoutInstance* instance, out LayoutTransformExtents extents)
    {
        extents = default;
        if (instance == null)
        {
            return false;
        }

        var vtable = *(nint**)instance;
        if (vtable == null)
        {
            return false;
        }

        var function = (delegate* unmanaged<ILayoutInstance*, LayoutTransformExtents*, LayoutTransformExtents*>)vtable[LayoutGetTransformExtentsVtableSlot];
        if (function == null)
        {
            return false;
        }

        LayoutTransformExtents result = default;
        if (function(instance, &result) == null)
        {
            return false;
        }

        extents = result;
        return true;
    }

    private static bool HasActiveChildColliders(ChildNodeContainer* container)
    {
        foreach (var child in container->Instances)
        {
            var node = child.Value;
            if (node == null || node->Instance == null)
            {
                continue;
            }

            var instance = node->Instance;
            if (instance->IsColliderActive())
            {
                return true;
            }

            if (instance->Id.Type == InstanceType.SharedGroup
                && HasActiveChildColliders(&((SharedGroupLayoutInstance*)instance)->Instances))
            {
                return true;
            }
        }

        return false;
    }

    private static bool InstanceTreeContainsCollider(ILayoutInstance* instance, Collider* collider)
    {
        if (instance == null || collider == null)
        {
            return false;
        }

        if (InstanceUsesCollider(instance, collider)
            || InstanceMatchesColliderLayoutId(instance, collider->LayoutObjectId))
        {
            return true;
        }

        return instance->Id.Type == InstanceType.SharedGroup
            && ChildContainerContainsCollider(&((SharedGroupLayoutInstance*)instance)->Instances, collider);
    }

    private static bool InstanceUsesCollider(ILayoutInstance* instance, Collider* collider)
        => instance->GetCollider() == collider
           || instance->GetCollider2() == collider;

    private static bool InstanceMatchesColliderLayoutId(ILayoutInstance* instance, ulong layoutObjectId)
    {
        if (layoutObjectId == 0)
        {
            return false;
        }

        uint instanceKey = (uint)layoutObjectId;
        uint subId = (uint)(layoutObjectId >> 32);
        return instance->Id.InstanceKey == instanceKey
            && (subId == 0 || instance->SubId == subId);
    }

    private static bool ChildContainerContainsCollider(ChildNodeContainer* container, Collider* collider)
    {
        foreach (var child in container->Instances)
        {
            var node = child.Value;
            if (node == null || node->Instance == null)
            {
                continue;
            }

            if (InstanceTreeContainsCollider(node->Instance, collider))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryResolveColliderLayoutInstance(Collider* collider, out ILayoutInstance* instance)
    {
        instance = null;
        if (collider == null)
        {
            return false;
        }

        foreach (InstanceType type in ColliderOwnerTypes)
        {
            instance = LayoutWorld.GetColliderLayoutInstance(type, collider);
            if (instance != null)
            {
                return true;
            }
        }

        return false;
    }

    private static bool InstanceTreeContainsInstance(ILayoutInstance* instance, ILayoutInstance* target)
    {
        if (instance == null || target == null)
        {
            return false;
        }

        if (instance == target)
        {
            return true;
        }

        return instance->Id.Type == InstanceType.SharedGroup
            && ChildContainerContainsInstance(&((SharedGroupLayoutInstance*)instance)->Instances, target);
    }

    private static bool ChildContainerContainsInstance(ChildNodeContainer* container, ILayoutInstance* target)
    {
        foreach (var child in container->Instances)
        {
            var node = child.Value;
            if (node == null || node->Instance == null)
            {
                continue;
            }

            if (InstanceTreeContainsInstance(node->Instance, target))
            {
                return true;
            }
        }

        return false;
    }

    private static void AccumulateSharedGroupLocalBounds(
        ChildNodeContainer* container,
        Matrix4x4 inverseRootTransform,
        ref bool hasLocalBounds,
        ref Vector3 localMin,
        ref Vector3 localMax)
    {
        foreach (var child in container->Instances)
        {
            var node = child.Value;
            if (node == null || node->Instance == null)
            {
                continue;
            }

            AccumulateInstanceLocalBounds(node->Instance, inverseRootTransform, ref hasLocalBounds, ref localMin, ref localMax);
        }
    }

    private static void AccumulateInstanceLocalBounds(
        ILayoutInstance* instance,
        Matrix4x4 inverseRootTransform,
        ref bool hasLocalBounds,
        ref Vector3 localMin,
        ref Vector3 localMax)
    {
        if (ShouldSkipSharedGroupBoundsInstance(instance->Id.Type))
        {
            return;
        }

        var primaryGraphics = instance->GetGraphics();
        AccumulateGraphicsLocalBounds(primaryGraphics, inverseRootTransform, ref hasLocalBounds, ref localMin, ref localMax);

        var secondaryGraphics = instance->GetGraphics2();
        if (secondaryGraphics != null && secondaryGraphics != primaryGraphics)
        {
            AccumulateGraphicsLocalBounds(secondaryGraphics, inverseRootTransform, ref hasLocalBounds, ref localMin, ref localMax);
        }

        if (instance->Id.Type == InstanceType.SharedGroup)
        {
            var childGroup = (SharedGroupLayoutInstance*)instance;
            AccumulateSharedGroupLocalBounds(&childGroup->Instances, inverseRootTransform, ref hasLocalBounds, ref localMin, ref localMax);
        }
    }

    private static void AccumulateGraphicsLocalBounds(
        SceneObject* graphics,
        Matrix4x4 inverseRootTransform,
        ref bool hasLocalBounds,
        ref Vector3 localMin,
        ref Vector3 localMax)
    {
        if (!ObjectSceneInterop.TryGetDrawObject(graphics, out var drawObject)
            || !ObjectSceneInterop.TryGetDrawObjectOrientedBounds(drawObject, out var bounds))
        {
            return;
        }

        Span<Vector3> worldCorners = stackalloc Vector3[8];
        ObjectShapeMath.CopyOrientedBoxCorners(bounds, worldCorners);
        foreach (var worldCorner in worldCorners)
        {
            ExpandBounds(Vector3.Transform(worldCorner, inverseRootTransform), ref hasLocalBounds, ref localMin, ref localMax);
        }
    }

    private static void ExpandBounds(Vector3 point, ref bool hasBounds, ref Vector3 min, ref Vector3 max)
    {
        if (!hasBounds)
        {
            min = point;
            max = point;
            hasBounds = true;
            return;
        }

        min = Vector3.Min(min, point);
        max = Vector3.Max(max, point);
    }

    private static bool ShouldSkipSharedGroupBoundsInstance(InstanceType type)
        => type is InstanceType.Light
            or InstanceType.Sound
            or InstanceType.TargetMarker
            or InstanceType.ChairMarker
            or InstanceType.SphereCastRange;
}


