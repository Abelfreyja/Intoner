using Intoner.Objects.Interop;
using System.Numerics;
using System.Runtime.InteropServices;
using AxisAlignedBounds = FFXIVClientStructs.FFXIV.Common.Math.AxisAlignedBounds;
using SceneBgObject = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.BgObject;

namespace Intoner.Objects.Runtime;

[StructLayout(LayoutKind.Auto)]
internal struct NativeBoundsInput
{
    public readonly nint Graphics;
    public readonly nint ModelResourceHandle;
    private readonly Vector3 _position;
    private readonly Quaternion _rotation;
    private readonly Vector3 _scale;
    private readonly Vector3 _min;
    private readonly Vector3 _max;

    private NativeBoundsInput(nint graphics, nint modelResourceHandle, Vector3 position, Quaternion rotation, Vector3 scale, Vector3 min, Vector3 max)
    {
        Graphics = graphics;
        ModelResourceHandle = modelResourceHandle;
        _position = position;
        _rotation = rotation;
        _scale = scale;
        _min = min;
        _max = max;
    }

    public bool ObserveTransform(Vector3 position, Quaternion rotation, Vector3 scale)
    {
        bool changed = Graphics != 0 || ModelResourceHandle != 0
            || !_position.Equals(position) || !_rotation.Equals(rotation) || !_scale.Equals(scale);
        if (changed)
        {
            this = new NativeBoundsInput(0, 0, position, rotation, scale, default, default);
        }
        return changed;
    }

    public unsafe bool TryObserve(SceneBgObject* graphics, out bool changed)
    {
        if (!BgObjectSceneInterop.IsModelLoaded(graphics) || graphics->ModelResourceHandle->AxisAlignedBounds == null)
        {
            this = default;
            changed = true;
            return false;
        }

        AxisAlignedBounds* bounds = graphics->ModelResourceHandle->AxisAlignedBounds;
        changed = Graphics != (nint)graphics || ModelResourceHandle != (nint)graphics->ModelResourceHandle
            || !_position.Equals(graphics->Position) || !_rotation.Equals(graphics->Rotation)
            || !_scale.Equals(graphics->Scale) || !_min.Equals(bounds->Min) || !_max.Equals(bounds->Max);
        if (changed)
        {
            this = new NativeBoundsInput((nint)graphics, (nint)graphics->ModelResourceHandle,
                graphics->Position, graphics->Rotation, graphics->Scale, bounds->Min, bounds->Max);
        }
        return true;
    }
}
