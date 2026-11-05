using Intoner.Objects.Resources;
using Lumina.Data.Files;
using Lumina.Data.Parsing.Layer;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;

namespace Intoner.Objects.Assets;

internal static class HousingMaterialUtility
{
    public static byte GetWallpaperInstanceId(SgbFile file)
        => GetWallpaperInstanceId(file.Data);

    public static byte GetWallpaperInstanceId(ReadOnlySpan<byte> data)
    {
        try
        {
            LayoutFileReader reader = new(data, "furniture material");
            if (reader.Read<FileHeader>(0).Magic != 0x31424753)
            {
                return 0;
            }

            byte instanceId = 0;
            reader.VisitSections((section, magic) =>
            {
                if (magic != 0x314E4353)
                {
                    return;
                }

                int housingOffset = section.Read<int>(0x2C);
                if (housingOffset == 0)
                {
                    return;
                }

                int housing = section.RelativeOffset(0, housingOffset, 0x30);
                int slotsOffset = section.Read<int>(housing + 0x2C);
                if (slotsOffset == 0)
                {
                    return;
                }

                int slots = section.RelativeOffset(housing, slotsOffset, 36);
                if ((section.Read<uint>(slots) & 0xF) == 7)
                {
                    instanceId = section.Read<byte>(slots + 4);
                }
            }, CancellationToken.None);
            return instanceId;
        }
        catch (InvalidDataException)
        {
            return 0;
        }
    }

    public static string GetWallpaperModelPath(SgbFile file)
    {
        byte instanceId = GetWallpaperInstanceId(file);
        if (instanceId == 0 || file.LayerGroups is null)
        {
            return string.Empty;
        }

        foreach (var group in file.LayerGroups)
        {
            foreach (var layer in group.Layers)
            {
                foreach (var instance in layer.InstanceObjects)
                {
                    if (instance.InstanceId == instanceId && instance.Object is LayerCommon.BGInstanceObject bg)
                    {
                        return ObjectResolvedPath.FromGamePath(bg.AssetPath).Path;
                    }
                }
            }
        }

        return string.Empty;
    }
}
