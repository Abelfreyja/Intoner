using Intoner.Objects.Assets;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace Intoner.Objects.Preview;

internal sealed class PreviewAsset
{
    public PreviewAsset(
        Key key,
        string displayPath,
        IReadOnlyList<PreviewModelInfo> models,
        Vector3 untexturedDiffuseColor)
    {
        AssetKey                = key;
        DisplayPath             = displayPath;
        Models                  = models;
        UntexturedDiffuseColor  = untexturedDiffuseColor;
        ModelSignature          = BuildModelSignature(models);
        MaterialSignature       = BuildMaterialSignature(untexturedDiffuseColor);
        CacheKey                = $"{key.Source}:{key.Identifier}:{ModelSignature}:{MaterialSignature}";
    }

    public Key AssetKey { get; }
    public string DisplayPath { get; }
    public IReadOnlyList<PreviewModelInfo> Models { get; }
    public Vector3 UntexturedDiffuseColor { get; }
    public string ModelSignature { get; }
    public string MaterialSignature { get; }
    public string CacheKey { get; }

    public bool IsValid
        => AssetKey.IsValid;

    public bool HasModels
        => Models.Count > 0;

    public string CreateTextureName(string prefix)
    {
        string fileName = Path.GetFileNameWithoutExtension(DisplayPath);
        return string.IsNullOrWhiteSpace(fileName)
            ? $"{prefix}.{AssetKey.Source}.{AssetKey.Identifier.GetHashCode(StringComparison.Ordinal):X8}"
            : $"{prefix}.{AssetKey.Source}.{fileName}";
    }

    private static string BuildModelSignature(IReadOnlyList<PreviewModelInfo> models)
    {
        if (models.Count == 0)
        {
            return "empty";
        }

        StringBuilder builder = new();
        for (var i = 0; i < models.Count; i++)
        {
            PreviewModelInfo model = models[i];
            if (i > 0)
            {
                builder.Append('|');
            }

            builder.Append(GameAssetPathRules.NormalizeGamePath(model.ModelPath));
            builder.Append('@');
            AppendMatrixKey(builder, model.Transform);
        }

        return builder.ToString();
    }

    private static string BuildMaterialSignature(Vector3 untexturedDiffuseColor)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{BitConverter.SingleToInt32Bits(untexturedDiffuseColor.X):X8},{BitConverter.SingleToInt32Bits(untexturedDiffuseColor.Y):X8},{BitConverter.SingleToInt32Bits(untexturedDiffuseColor.Z):X8}");
    }

    private static void AppendMatrixKey(StringBuilder builder, Matrix4x4 matrix)
    {
        AppendFloatKey(builder, matrix.M11);
        AppendFloatKey(builder, matrix.M12);
        AppendFloatKey(builder, matrix.M13);
        AppendFloatKey(builder, matrix.M14);
        AppendFloatKey(builder, matrix.M21);
        AppendFloatKey(builder, matrix.M22);
        AppendFloatKey(builder, matrix.M23);
        AppendFloatKey(builder, matrix.M24);
        AppendFloatKey(builder, matrix.M31);
        AppendFloatKey(builder, matrix.M32);
        AppendFloatKey(builder, matrix.M33);
        AppendFloatKey(builder, matrix.M34);
        AppendFloatKey(builder, matrix.M41);
        AppendFloatKey(builder, matrix.M42);
        AppendFloatKey(builder, matrix.M43);
        AppendFloatKey(builder, matrix.M44);
    }

    private static void AppendFloatKey(StringBuilder builder, float value)
    {
        builder.Append(BitConverter.SingleToInt32Bits(value).ToString("X8", CultureInfo.InvariantCulture));
        builder.Append(',');
    }

    internal readonly record struct Key(string Source, string Identifier)
    {
        public bool IsValid
            => !string.IsNullOrWhiteSpace(Source)
            && !string.IsNullOrWhiteSpace(Identifier);
    }
}
