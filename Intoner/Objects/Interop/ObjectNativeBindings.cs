using Dalamud.Plugin.Services;
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
        public FurnitureBinding(nint createAddress, nint destroyAddress, nint applyStateAddress)
        {
            CreateAddress = createAddress;
            DestroyAddress = destroyAddress;
            ApplyStateAddress = applyStateAddress;
        }

        public nint CreateAddress { get; }
        public nint DestroyAddress { get; }
        public nint ApplyStateAddress { get; }

        public bool IsAvailable
            => CreateAddress != nint.Zero && DestroyAddress != nint.Zero && ApplyStateAddress != nint.Zero;
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

        public bool TryApplyPlaybackState(SceneVfxObject* vfxObject, float speed, bool paused)
        {
            if (vfxObject == null || vfxObject->VfxResourceInstance == null)
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
#if DEBUG
        ObjectSignatures.TestSignatures(sigScanner, _logger);
#endif
        Furniture = CreateFurnitureBinding(sigScanner);
        Vfx = CreateVfxBinding(sigScanner);
    }

    public FurnitureBinding Furniture { get; }
    public VfxBinding Vfx { get; }

    private VfxBinding CreateVfxBinding(ISigScanner sigScanner)
    {
        nint pauseToggleAddress = ObjectNativeAddressResolver.TryResolveJmpCallTarget(
            _logger,
            sigScanner,
            ObjectSignatures.NativeVfxPauseToggle);
        nint isPausedAddress = ObjectNativeAddressResolver.TryResolveJmpCallTarget(
            _logger,
            sigScanner,
            ObjectSignatures.NativeVfxIsPaused);
        nint setSpeedAddress = ObjectNativeAddressResolver.TryScanSingleTextMatch(
            _logger,
            sigScanner,
            ObjectSignatures.NativeVfxSetSpeed.Signature,
            ObjectSignatures.NativeVfxSetSpeed.Label);

        return new VfxBinding(pauseToggleAddress, isPausedAddress, setSpeedAddress);
    }

    private FurnitureBinding CreateFurnitureBinding(ISigScanner sigScanner)
    {
        // builds the descriptor, allocates SharedGroupLayoutInstance, initializes it, then enters native create
        nint createAddress = ObjectNativeAddressResolver.TryResolveJmpCallTarget(
            _logger,
            sigScanner,
            ObjectSignatures.NativeFurnitureCreate);

        // handles helper unregister work, then calls cleanup, destructor, and free
        nint destroyAddress = ObjectNativeAddressResolver.TryResolveJmpCallTarget(
            _logger,
            sigScanner,
            ObjectSignatures.NativeFurnitureDestroy);

        // writes shared group housing state, updates layout world state, and invokes the collider state callback
        nint applyStateAddress = ObjectNativeAddressResolver.TryResolveJmpCallTarget(
            _logger,
            sigScanner,
            ObjectSignatures.NativeFurnitureApplyState);

        if (createAddress == nint.Zero || destroyAddress == nint.Zero || applyStateAddress == nint.Zero)
        {
            return default;
        }

        return new FurnitureBinding(createAddress, destroyAddress, applyStateAddress);
    }
}


