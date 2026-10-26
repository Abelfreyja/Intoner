using Intoner.Objects.Resources;
using Lumina.Data.Parsing;
using Penumbra.GameData.Data;
using Penumbra.GameData.Files;
using Penumbra.GameData.Files.Materials;

namespace Intoner.Objects.Assets;

internal static class TextureMapKindResolver
{
    private static readonly (TextureMapKind Kind, string Token)[] MapTokens =
    {
        (TextureMapKind.Normal, "_n."),
        (TextureMapKind.Normal, "_n_"),
        (TextureMapKind.Normal, "_normal"),
        (TextureMapKind.Normal, "normal_"),
        (TextureMapKind.Normal, "_norm"),
        (TextureMapKind.Normal, "norm_"),

        (TextureMapKind.Mask, "_m."),
        (TextureMapKind.Mask, "_m_"),
        (TextureMapKind.Mask, "_mask"),
        (TextureMapKind.Mask, "mask_"),
        (TextureMapKind.Mask, "_msk"),

        (TextureMapKind.Specular, "_s."),
        (TextureMapKind.Specular, "_s_"),
        (TextureMapKind.Specular, "_spec"),
        (TextureMapKind.Specular, "_specular"),
        (TextureMapKind.Specular, "specular_"),

        (TextureMapKind.Index, "_id."),
        (TextureMapKind.Index, "_id_"),
        (TextureMapKind.Index, "_idx"),
        (TextureMapKind.Index, "_index"),
        (TextureMapKind.Index, "index_"),
        (TextureMapKind.Index, "_multi"),

        (TextureMapKind.Diffuse, "_d."),
        (TextureMapKind.Diffuse, "_d_"),
        (TextureMapKind.Diffuse, "_diff"),
        (TextureMapKind.Diffuse, "_b."),
        (TextureMapKind.Diffuse, "_b_"),
        (TextureMapKind.Diffuse, "_base"),
        (TextureMapKind.Diffuse, "base_")
    };

    private const uint NormalSamplerId = ShpkFile.NormalSamplerId;
    private const uint IndexSamplerId = ShpkFile.IndexSamplerId;
    private const uint SpecularSamplerId = ShpkFile.SpecularSamplerId;
    private const uint DiffuseSamplerId = ShpkFile.DiffuseSamplerId;
    private const uint MaskSamplerId = ShpkFile.MaskSamplerId;

    public static bool TryGetTexturePath(MtrlFile material, TextureMapKind kind, out string texturePath)
    {
        texturePath = string.Empty;

        foreach (Sampler sampler in material.ShaderPackage.Samplers)
        {
            if (!TryMapSamplerId(sampler.SamplerId, out TextureMapKind candidateKind) || candidateKind != kind)
            {
                continue;
            }

            if (!TryResolveTexturePath(material, sampler.TextureIndex, out texturePath))
            {
                continue;
            }

            return true;
        }

        for (int textureIndex = 0; textureIndex < material.Textures.Length; textureIndex++)
        {
            bool hasKnownSampler = false;
            foreach (Sampler sampler in material.ShaderPackage.Samplers)
            {
                if (sampler.TextureIndex != textureIndex || !TryMapSamplerId(sampler.SamplerId, out _))
                {
                    continue;
                }

                hasKnownSampler = true;
                break;
            }

            if (hasKnownSampler || !TryResolveTexturePath(material, textureIndex, out string candidatePath))
            {
                continue;
            }

            TextureMapKind candidateKind = GuessFromFileName(candidatePath);

            if (candidateKind == kind
             || (kind == TextureMapKind.Diffuse && candidateKind == TextureMapKind.Unknown && material.Textures.Length == 1))
            {
                texturePath = candidatePath;
                return true;
            }
        }

        return false;
    }

    public static TextureMapKind GuessFromFileName(string path)
    {
        var normalized = NormalizeLookupPath(path);
        var fileNameWithExtension = Path.GetFileName(normalized);
        var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(normalized);
        if (string.IsNullOrEmpty(fileNameWithExtension) && string.IsNullOrEmpty(fileNameWithoutExtension))
            return TextureMapKind.Unknown;

        if (normalized.Contains("/eye/eyelids_shadow.tex", StringComparison.Ordinal))
            return TextureMapKind.Normal;

        if (normalized.StartsWith("ui/map/", StringComparison.Ordinal) && !string.IsNullOrEmpty(fileNameWithoutExtension))
        {
            if (fileNameWithoutExtension.EndsWith("m_m", StringComparison.Ordinal)
                || fileNameWithoutExtension.EndsWith("m_s", StringComparison.Ordinal))
                return TextureMapKind.Mask;

            if (fileNameWithoutExtension.EndsWith("_m", StringComparison.Ordinal)
                || fileNameWithoutExtension.EndsWith("_s", StringComparison.Ordinal)
                || fileNameWithoutExtension.EndsWith("d", StringComparison.Ordinal))
                return TextureMapKind.Diffuse;
        }

        foreach (var (kind, token) in MapTokens)
        {
            if (!string.IsNullOrEmpty(fileNameWithExtension) &&
                fileNameWithExtension.Contains(token, StringComparison.OrdinalIgnoreCase))
                return kind;
        }

        return TextureMapKind.Unknown;
    }

    public static bool TryMapSamplerId(uint id, out TextureMapKind kind)
    {
        kind = id switch
        {
            NormalSamplerId => TextureMapKind.Normal,
            IndexSamplerId => TextureMapKind.Index,
            SpecularSamplerId => TextureMapKind.Specular,
            DiffuseSamplerId => TextureMapKind.Diffuse,
            MaskSamplerId => TextureMapKind.Mask,
            _ => TextureMapKind.Unknown
        };

        return kind != TextureMapKind.Unknown;
    }

    public static bool TryResolveTexturePath(MtrlFile material, int textureIndex, out string texturePath)
    {
        texturePath = string.Empty;
        if (textureIndex < 0 || textureIndex >= material.Textures.Length)
            return false;

        return TryResolveTexturePath(material.Textures[textureIndex], out texturePath);
    }

    public static bool TryResolveTexturePath(in MtrlFile.Texture texture, out string texturePath)
    {
        MtrlFile.Texture normalizedTexture = texture;
        normalizedTexture.Path = ObjectResolvedPath.FromGamePath(texture.Path).Path;

        if (normalizedTexture.Path.Length == 0)
        {
            texturePath = string.Empty;
            return false;
        }

        if (string.Equals(normalizedTexture.Path, "dummy.tex", StringComparison.OrdinalIgnoreCase))
        {
            texturePath = GamePaths.Tex.Dummy;
            return true;
        }

        _ = GamePaths.Tex.HandleDx11Path(normalizedTexture, out string path);
        texturePath = ObjectResolvedPath.FromGamePath(path).Path;
        return texturePath.Length > 0;
    }

    internal static string NormalizeLookupPath(string? path)
        => NormalizeGamePath(path).ToLowerInvariant();

    internal static string NormalizeGamePath(string? path)
        => GameAssetPathRules.NormalizeGamePath(path ?? string.Empty);
}
