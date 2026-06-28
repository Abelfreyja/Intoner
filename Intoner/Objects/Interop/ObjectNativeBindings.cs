using Dalamud.Plugin.Services;
using Microsoft.Extensions.Logging;

namespace Intoner.Objects.Interop;

internal sealed class ObjectNativeBindings
{
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

    private readonly ILogger<ObjectNativeBindings> _logger;

    public ObjectNativeBindings(ILogger<ObjectNativeBindings> logger, ISigScanner sigScanner)
    {
        _logger = logger;
#if DEBUG
        ObjectSignatures.TestSignatures(sigScanner, _logger);
#endif
        Furniture = CreateFurnitureBinding(sigScanner);
    }

    public FurnitureBinding Furniture { get; }

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


