using Dalamud.Plugin.Services;
using Dalamud.Utility.Signatures;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;

namespace Intoner.Objects.Resources;

internal sealed unsafe class ObjectTextureLodService
{
    [Signature(ObjectSignatures.LodConfig)]
    private readonly nint _lodConfig = nint.Zero;

    public ObjectTextureLodService(IGameInteropProvider gameInteropProvider)
        => gameInteropProvider.InitializeFromAttributes(this);

    public byte GetLod(TextureResourceHandle* handle)
    {
        if (handle != null && _lodConfig != nint.Zero && handle->ChangeLod)
        {
            var config = *(byte*)_lodConfig + 0xE;
            if (config == byte.MaxValue)
            {
                return 2;
            }
        }

        return 0;
    }
}

