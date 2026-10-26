using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Group;
using Intoner.Objects.Models;
using Intoner.Objects.Utils;
using Intoner.Services.Interop;
using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;
using SceneVfxObject = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.VfxObject;
using VfxResourceInstance = FFXIVClientStructs.FFXIV.Client.Graphics.Vfx.VfxResourceInstance;

namespace Intoner.Objects.Interop;

internal sealed unsafe class ObjectNativeBindings
{
    [StructLayout(LayoutKind.Auto)]
    public readonly struct FurnitureBinding
    {
        private const int HelperSubtype = 0x0C;
        private readonly delegate* unmanaged<short, uint, Transform*, byte*, byte*, byte, uint, int, nint, nint, SharedGroupLayoutInstance*> _create;
        private readonly delegate* unmanaged<SharedGroupLayoutInstance**, nint, void> _destroy;

        public FurnitureBinding(nint createAddress, nint destroyAddress)
        {
            _create = (delegate* unmanaged<short, uint, Transform*, byte*, byte*, byte, uint, int, nint, nint, SharedGroupLayoutInstance*>)createAddress;
            _destroy = (delegate* unmanaged<SharedGroupLayoutInstance**, nint, void>)destroyAddress;
        }

        public bool IsAvailable
            => _create != null && _destroy != null;

        public SharedGroupLayoutInstance* Create(Transform* transform, byte* path)
            => _create(-1, uint.MaxValue, transform, path, null, 1, 0, HelperSubtype, 0, 0);

        public void Destroy(SharedGroupLayoutInstance* instance)
            => _destroy(&instance, 0);
    }

    [StructLayout(LayoutKind.Auto)]
    public readonly struct VfxBinding
    {
        private readonly nint _pauseToggleAddress;
        private readonly nint _isPausedAddress;
        private readonly nint _setSpeedAddress;

        public VfxBinding(nint pauseToggleAddress, nint isPausedAddress, nint setSpeedAddress)
        {
            _pauseToggleAddress = pauseToggleAddress;
            _isPausedAddress = isPausedAddress;
            _setSpeedAddress = setSpeedAddress;
        }

        public bool SupportsPlaybackState(float speed, bool paused)
            => (_setSpeedAddress != nint.Zero || NumericsUtility.IsNearlyEqual(speed, VfxModel.DefaultSpeed))
                && ((_pauseToggleAddress != nint.Zero && _isPausedAddress != nint.Zero) || !paused);

        public bool TryApplyPlaybackState(SceneVfxObject* vfxObject, float speed, bool paused)
        {
            if (!SupportsPlaybackState(speed, paused)
                || vfxObject == null || vfxObject->VfxResourceInstance == null)
            {
                return false;
            }

            if (_setSpeedAddress != nint.Zero)
            {
                var setSpeed = (delegate* unmanaged<SceneVfxObject*, float, void>)_setSpeedAddress;
                setSpeed(vfxObject, speed);
            }

            if (_pauseToggleAddress != nint.Zero && _isPausedAddress != nint.Zero)
            {
                var resourceInstance = vfxObject->VfxResourceInstance;
                var isPaused = (delegate* unmanaged<VfxResourceInstance*, byte>)_isPausedAddress;
                if ((isPaused(resourceInstance) != 0) != paused)
                {
                    var togglePause = (delegate* unmanaged<VfxResourceInstance*, void>)_pauseToggleAddress;
                    togglePause(resourceInstance);
                }
            }

            return true;
        }
    }

    private readonly ILogger<ObjectNativeBindings> _logger;

    public ObjectNativeBindings(ILogger<ObjectNativeBindings> logger, ISigScanner sigScanner)
    {
        _logger = logger;
        Furniture = CreateFurnitureBinding(sigScanner);
        Vfx = CreateVfxBinding(sigScanner);
    }

    public FurnitureBinding Furniture { get; }
    public VfxBinding Vfx { get; }

    private VfxBinding CreateVfxBinding(ISigScanner sigScanner)
    {
        nint pauseToggleAddress = NativeAddressResolver.TryResolveJmpCallTarget(
            _logger,
            sigScanner,
            IntonerSignatures.NativeVfxPauseToggle);
        nint isPausedAddress = NativeAddressResolver.TryResolveJmpCallTarget(
            _logger,
            sigScanner,
            IntonerSignatures.NativeVfxIsPaused);
        nint setSpeedAddress = NativeAddressResolver.TryScanSingleTextMatch(
            _logger,
            sigScanner,
            IntonerSignatures.NativeVfxSetSpeed.Signature,
            IntonerSignatures.NativeVfxSetSpeed.Label);

        return new VfxBinding(pauseToggleAddress, isPausedAddress, setSpeedAddress);
    }

    private FurnitureBinding CreateFurnitureBinding(ISigScanner sigScanner)
    {
        // builds the descriptor, allocates SharedGroupLayoutInstance, initializes it, then enters native create
        nint createAddress = NativeAddressResolver.TryResolveJmpCallTarget(
            _logger,
            sigScanner,
            IntonerSignatures.NativeFurnitureCreate);

        // handles helper unregister work, then calls cleanup, destructor, and free
        nint destroyAddress = NativeAddressResolver.TryResolveJmpCallTarget(
            _logger,
            sigScanner,
            IntonerSignatures.NativeFurnitureDestroy);

        if (createAddress == nint.Zero || destroyAddress == nint.Zero)
        {
            return default;
        }

        return new FurnitureBinding(createAddress, destroyAddress);
    }
}
