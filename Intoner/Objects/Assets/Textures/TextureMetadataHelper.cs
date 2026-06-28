using Penumbra.GameData.Files;

namespace Intoner.Objects.Assets;

internal static class TextureMetadataHelper
{
    public static bool TryGetTexturePath(MtrlFile material, TextureMapKind kind, out string texturePath)
        => TextureMapKindResolver.TryGetTexturePath(material, kind, out texturePath);
}
