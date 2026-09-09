using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using Intoner.Services.Interop;
using Microsoft.Extensions.Logging;

namespace Intoner.Objects.Resources;

internal sealed unsafe class ObjectTextureLodService
{
    private readonly nint _lodConfig;

    public ObjectTextureLodService(ILogger<ObjectTextureLodService> logger, ISigScanner sigScanner)
        => _lodConfig = NativeAddressResolver.TryResolveStaticAddress(logger, sigScanner, IntonerSignatures.ResourceLodConfig);

    public byte GetLod(TextureResourceHandle* handle)
    {
        if (handle == null || !handle->ChangeLod || _lodConfig == nint.Zero)
        {
            return 0;
        }

        byte* config = *(byte**)_lodConfig;
        if (config == null)
        {
            return 0;
        }

        byte lod = config[0xE];
        return lod == byte.MaxValue ? (byte)2 : lod;
    }
}

