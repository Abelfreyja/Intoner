using Dalamud.Plugin.Services;
using Intoner.Objects.Assets;
using Intoner.Objects.Interop;
using Intoner.Objects.Models;
using Intoner.Objects.Resources;
using Microsoft.Extensions.Logging;
using System.Numerics;
using DrawObject = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.DrawObject;
using SceneVfxObject = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.VfxObject;

namespace Intoner.Objects.Runtime;

internal sealed unsafe class VfxSceneObject : DrawSceneObject
{
    private const byte SomeFlagsClearBit3Mask = 0xF7;
    private const int VfxResourceInstanceUnkOffset = 0x08;
    private const int VfxResourceUnkApricotHandleOffset = 0x18;

    private SceneVfxObject* _vfxObject;
    private string _vfxPath = string.Empty;
    private readonly IObjectResourceTracker _resourceTracker;
    private bool _needsVisualReplay;
    private bool _needsInitialVisualReplay = true;
    private ObjectResourceRegistration _rootHandleRegistration;

    public override ObjectKind Kind
        => ObjectKind.Vfx;

    public override bool NeedsFrameworkUpdates
        => _needsVisualReplay || (Snapshot.CollectionId.Length > 0 && !_rootHandleRegistration.IsRegistered);

    public override nint Address
        => (nint)_vfxObject;

    protected override DrawObject* DrawObjectPointer
        => _vfxObject != null ? (DrawObject*)_vfxObject : null;

    internal VfxSceneObject(
        IFramework framework,
        ILogger logger,
        ObjectSnapshot snapshot,
        SceneVfxObject* vfxObject,
        string vfxPath,
        IObjectResourceTracker resourceTracker)
        : base(framework, logger, snapshot)
    {
        _vfxObject = vfxObject;
        _vfxPath = vfxPath;
        _resourceTracker = resourceTracker;
        UpdateRegisteredRootHandle(snapshot);
    }

    protected override void FrameworkUpdateUnsafe()
    {
        if (_vfxObject == null)
        {
            return;
        }

        if (_needsVisualReplay)
        {
            ApplyRuntimeStateUnsafe(Snapshot);
            if (TryApplyVisualStateUnsafe((VfxModel)Snapshot.Model))
            {
                _needsVisualReplay = false;
            }
        }

        UpdateRegisteredRootHandle(Snapshot);
    }

    protected override SceneObjectUpdateResult ValidateSnapshotUpdate(ObjectSnapshot snapshot)
    {
        var previousModel = (VfxModel)Snapshot.Model;
        var vfxModel = (VfxModel)snapshot.Model;

        if (_vfxObject == null)
        {
            return SceneObjectUpdateResult.RequiresRecreate;
        }

        if (string.IsNullOrWhiteSpace(vfxModel.VfxPath) || !ObjectPathRules.IsVfxPath(vfxModel.VfxPath))
        {
            Logger.LogDebug("skipping vfx update because path is empty or invalid");
            return SceneObjectUpdateResult.Rejected;
        }

        if (!string.Equals(Snapshot.CollectionId, snapshot.CollectionId, StringComparison.OrdinalIgnoreCase))
        {
            return SceneObjectUpdateResult.RequiresRecreate;
        }

        return string.Equals(previousModel.VfxPath, vfxModel.VfxPath, StringComparison.OrdinalIgnoreCase)
            ? SceneObjectUpdateResult.Applied
            : SceneObjectUpdateResult.RequiresRecreate;
    }

    protected override SceneObjectUpdateResult ApplySnapshotUnsafe(ObjectSnapshot snapshot, ObjectSnapshot previousSnapshot)
    {
        if (_vfxObject == null)
        {
            return SceneObjectUpdateResult.RequiresRecreate;
        }

        var vfxModel = (VfxModel)snapshot.Model;
        var previousModel = (VfxModel)previousSnapshot.Model;
        var needsVisualReplay =
            // keep the first bootstrap update so the created vfx gets one visual apply pass,
            // but do not replay the effect for normal transform or visibility changes otherwise
            _needsInitialVisualReplay
            || !string.Equals(vfxModel.VfxPath, previousModel.VfxPath, StringComparison.OrdinalIgnoreCase)
            || vfxModel.NeedsVisualState(previousModel);

        ApplyRuntimeStateUnsafe(snapshot);
        if (!needsVisualReplay)
        {
            _needsVisualReplay = false;
            return SceneObjectUpdateResult.Applied;
        }

        _needsVisualReplay = !TryApplyVisualStateUnsafe(vfxModel);
        _needsInitialVisualReplay = false;
        UpdateRegisteredRootHandle(snapshot);
        return SceneObjectUpdateResult.Applied;
    }

    protected override SceneObjectUpdateResult RefreshResourcesUnsafe(ObjectSnapshot snapshot)
        => SceneObjectUpdateResult.RequiresRecreate;

    public override void AppendSelectionDraws(ObjectSelectionCollector collector)
    {
        if (_vfxObject == null || !Snapshot.Visible)
        {
            return;
        }

        if (TryGetBounds(out var bounds))
        {
            var extents = bounds.Max - bounds.Min;
            var radius = MathF.Max(0.75f, MathF.Max(extents.X, MathF.Max(extents.Y, extents.Z)) * 0.5f);
            var center = (bounds.Min + bounds.Max) * 0.5f;
            collector.AddPrimitive(
                Snapshot,
                ObjectSelectionPrimitiveKind.Sphere,
                ObjectSelectionCollector.CreateWorldTransform(center, Quaternion.Identity, new Vector3(radius)));
            return;
        }

        var maxScale = MathF.Max(Snapshot.Transform.Scale.X, MathF.Max(Snapshot.Transform.Scale.Y, Snapshot.Transform.Scale.Z));
        var fallbackRadius = Math.Clamp(maxScale * 1.5f, 1f, 8f);
        collector.AddPrimitive(
            Snapshot,
            ObjectSelectionPrimitiveKind.Sphere,
            ObjectSelectionCollector.CreateWorldTransform(Snapshot.Transform.Position, Quaternion.Identity, new Vector3(fallbackRadius)));
    }

    protected override void DisposeUnsafe()
    {
        if (_vfxObject == null)
        {
            return;
        }

        Logger.LogInformation(
            "destroying vfx 0x{Address:X} using path {VfxPath}",
            (ulong)(nint)_vfxObject,
            _vfxPath);

        _rootHandleRegistration.Clear(_resourceTracker);
        _vfxObject->CleanupRender();
        _vfxObject->Dtor(DestroyFlagsFree);

        _vfxObject = null;
        _vfxPath = string.Empty;
        _needsVisualReplay = false;
        _needsInitialVisualReplay = false;
    }

    private void ApplyRuntimeStateUnsafe(ObjectSnapshot snapshot)
    {
        if (_vfxObject == null)
        {
            return;
        }

        _vfxObject->Position = snapshot.Transform.Position;
        _vfxObject->Rotation = CreateRotation(snapshot.Transform.RotationDegrees);
        _vfxObject->Scale = snapshot.Transform.Scale;

        var drawObject = (DrawObject*)_vfxObject;
        drawObject->IsVisible = snapshot.Visible;
        drawObject->NotifyTransformChanged();
        drawObject->UpdateCulling();
    }

    private bool TryApplyVisualStateUnsafe(VfxModel model)
    {
        if (_vfxObject == null)
        {
            return false;
        }

        var drawObject = (DrawObject*)_vfxObject;
        _vfxObject->SomeFlags &= SomeFlagsClearBit3Mask;
        _vfxObject->Color = model.Color;
        _vfxObject->Update(0.0f);
        return _vfxObject->VfxResourceInstance != null && ObjectSceneInterop.IsDrawObjectLoaded(drawObject);
    }

    private void UpdateRegisteredRootHandle(ObjectSnapshot snapshot)
    {
        if (_vfxObject == null || snapshot.CollectionId.Length == 0)
        {
            _rootHandleRegistration.Clear(_resourceTracker);
            return;
        }

        var currentRootHandle = ResolveRootHandleAddress(_vfxObject);
        _rootHandleRegistration.UpdateRootHandle(
            _resourceTracker,
            currentRootHandle,
            new ObjectResourceScope(snapshot.CollectionId, _vfxPath));
    }

    private static nint ResolveRootHandleAddress(SceneVfxObject* vfxObject)
    {
        if (vfxObject == null || vfxObject->VfxResourceInstance == null)
        {
            return nint.Zero;
        }

        var vfxResourceInstanceAddress = (byte*)vfxObject->VfxResourceInstance;
        var vfxResourceUnkAddress = *(nint*)(vfxResourceInstanceAddress + VfxResourceInstanceUnkOffset);
        if (vfxResourceUnkAddress == nint.Zero)
        {
            return nint.Zero;
        }

        return *(nint*)(vfxResourceUnkAddress + VfxResourceUnkApricotHandleOffset);
    }
}

