using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Group;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer;
using Intoner.Objects.Interop;
using Intoner.Objects.Models;
using System.Runtime.InteropServices;
using AxisAlignedBounds = FFXIVClientStructs.FFXIV.Common.Math.AxisAlignedBounds;
using OrientedBounds = FFXIVClientStructs.FFXIV.Common.Math.OrientedBounds;

namespace Intoner.Objects.Runtime;

internal sealed unsafe class FurnitureNativeState(ObjectFrameworkDiagnostics diagnostics)
{
    [StructLayout(LayoutKind.Auto)]
    private struct BoundsInput
    {
        public nint Parent;
        public InstanceType Type;
        public NativeBoundsInput Geometry;
    }

    private readonly List<nint> _instances = [];
    private readonly List<BoundsInput> _boundsInputs = [];
    private int _count;
    private bool _canReuseBounds;
    private bool _worldBoundsValid;
    private bool _orientedBoundsValid;
    private bool _clearanceValid;
    private bool _hasPlacementMarker;
    private bool _hasWorldBounds;
    private bool _hasOrientedBounds;
    private AxisAlignedBounds _worldBounds;
    private OrientedBounds _orientedBounds;
    private bool _hasClearance;
    private ObjectPlacementClearance _clearance;

    public IReadOnlyList<nint> Instances => _instances;
    public bool MembershipChanged { get; private set; }
    public bool VisualsChanged { get; private set; }
    public bool HasActiveColliders { get; private set; }

    public void Observe(SharedGroupLayoutInstance* instance, bool inspectColliders)
    {
        _count = 0;
        _canReuseBounds = true;
        MembershipChanged = false;
        VisualsChanged = false;
        HasActiveColliders = false;
        _hasPlacementMarker = false;
        ObserveInstance((ILayoutInstance*)instance, 0, inspectColliders && diagnostics.IsCapturing);
        if (_count < _instances.Count)
        {
            _instances.RemoveRange(_count, _instances.Count - _count);
            _boundsInputs.RemoveRange(_count, _boundsInputs.Count - _count);
            MembershipChanged = true;
            VisualsChanged = true;
            InvalidateBounds();
        }

        if (!_canReuseBounds)
        {
            InvalidateBounds();
        }
    }

    public void InvalidateBounds()
    {
        _worldBoundsValid = false;
        _orientedBoundsValid = false;
        _clearanceValid = false;
    }

    public bool TryGetBounds(SharedGroupLayoutInstance* instance, out AxisAlignedBounds bounds)
    {
        if (instance == null)
        {
            bounds = default;
            return false;
        }

        if (!_worldBoundsValid)
        {
            diagnostics.Count(ObjectFrameworkCounter.FurnitureWorldBoundsQueries);
            _hasWorldBounds = ObjectLayoutInterop.TryGetBounds((ILayoutInstance*)instance, out _worldBounds);
            _worldBoundsValid = _canReuseBounds;
        }

        bounds = _worldBounds;
        return _hasWorldBounds;
    }

    public bool TryGetOrientedBounds(SharedGroupLayoutInstance* instance, out OrientedBounds bounds)
    {
        if (instance == null)
        {
            bounds = default;
            return false;
        }

        if (!_orientedBoundsValid)
        {
            diagnostics.Count(ObjectFrameworkCounter.FurnitureOrientedBoundsQueries);
            _hasOrientedBounds = ObjectLayoutInterop.TryGetSharedGroupOrientedBounds(instance, out _orientedBounds);
            _orientedBoundsValid = _canReuseBounds;
        }

        bounds = _orientedBounds;
        return _hasOrientedBounds;
    }

    public bool TryGetPlacementClearance(SharedGroupLayoutInstance* instance, out ObjectPlacementClearance clearance)
    {
        if (instance == null)
        {
            clearance = default;
            return false;
        }

        if (!_clearanceValid)
        {
            diagnostics.Count(ObjectFrameworkCounter.FurnitureClearanceQueries);
            _hasClearance = ObjectLayoutInterop.TryGetSharedGroupPlacementClearance(instance, out _clearance);
            _clearanceValid = _canReuseBounds && !_hasPlacementMarker;
        }

        clearance = _clearance;
        return _hasClearance;
    }

    private void ObserveInstance(ILayoutInstance* instance, nint parent, bool inspectColliders)
    {
        if (instance == null)
        {
            return;
        }

        if (inspectColliders && !HasActiveColliders && instance->IsColliderActive())
        {
            HasActiveColliders = true;
        }

        _hasPlacementMarker |= instance->Id.Type == InstanceType.SphereCastRange;
        ObserveBoundsInput(instance, parent);
        if (instance->Id.Type != InstanceType.SharedGroup)
        {
            return;
        }

        SharedGroupLayoutInstance* group = (SharedGroupLayoutInstance*)instance;
        foreach (var child in group->Instances.Instances.AsSpan())
        {
            if (child.Value != null)
            {
                ObserveInstance(child.Value->Instance, (nint)instance, inspectColliders);
            }
        }
    }

    private void ObserveBoundsInput(ILayoutInstance* instance, nint parent)
    {
        int index = _count++;
        bool added = index == _instances.Count;

        if (added)
        {
            _instances.Add((nint)instance);
            _boundsInputs.Add(default);
        }

        ref BoundsInput previous = ref CollectionsMarshal.AsSpan(_boundsInputs)[index];
        bool membershipChanged = added || _instances[index] != (nint)instance || previous.Type != instance->Id.Type;
        bool parentChanged = previous.Parent != parent;
        nint previousGraphics = previous.Geometry.Graphics;
        nint previousModelResourceHandle = previous.Geometry.ModelResourceHandle;
        bool geometryChanged = ObserveGeometry(instance, ref previous);

        if (membershipChanged || parentChanged
            || previousGraphics != previous.Geometry.Graphics || previousModelResourceHandle != previous.Geometry.ModelResourceHandle)
        {
            VisualsChanged = true;
        }

        if (membershipChanged)
        {
            MembershipChanged = true;
            _instances[index] = (nint)instance;
        }

        if (membershipChanged || parentChanged)
        {
            previous.Parent = parent;
            previous.Type = instance->Id.Type;
        }

        if (membershipChanged || parentChanged || geometryChanged)
        {
            InvalidateBounds();
        }
    }

    private bool ObserveGeometry(ILayoutInstance* instance, ref BoundsInput input)
    {
        if (instance->Id.Type == InstanceType.SharedGroup)
        {
            SharedGroupLayoutInstance* group = (SharedGroupLayoutInstance*)instance;
            return input.Geometry.ObserveTransform(group->Transform.Translation, group->Transform.Rotation, group->Transform.Scale);
        }

        if (instance->Id.Type == InstanceType.BgPart)
        {
            _canReuseBounds &= input.Geometry.TryObserve(((BgPartsLayoutInstance*)instance)->GraphicsObject, out bool changed);
            return changed;
        }

        if (input.Type != instance->Id.Type)
        {
            input.Geometry = default;
        }

        _canReuseBounds &= instance->Id.Type is InstanceType.Sound or InstanceType.TargetMarker or InstanceType.ChairMarker or InstanceType.SphereCastRange;
        return false;
    }
}
