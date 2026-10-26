using Intoner.Objects.Models;
using Intoner.Objects.Resources;
using Intoner.Objects.Utils;

namespace Intoner.Objects.Assets;

internal static class BgColliderDefinitionUtility
{
    public static bool IsValid(BgColliderDefinition definition, string modelPath)
    {
        if (definition.ModelPath is null || definition.SourcePath is null || definition.PcbPath is null
         || !Enum.IsDefined(definition.Kind)
         || !string.Equals(definition.ModelPath, modelPath, StringComparison.OrdinalIgnoreCase)
         || !GameAssetPathRules.IsFileKind(definition.ModelPath, GameAssetFileKind.Mdl))
        {
            return false;
        }

        if (definition.Kind == BgCollisionKind.Mesh)
        {
            return definition.Shape is null
                && GameAssetPathRules.IsFileKind(definition.PcbPath, GameAssetFileKind.Pcb)
                && string.Equals(ObjectResolvedPath.FromGamePath(definition.PcbPath).Path, definition.PcbPath, StringComparison.Ordinal)
                && ObjectResourcePathEncoding.TryGetPcbPathByteCount(definition.PcbPath, out _);
        }

        if (definition.PcbPath.Length != 0)
        {
            return false;
        }

        if (definition.Kind == BgCollisionKind.None)
        {
            return definition.Shape is null;
        }

        return definition.Shape is { } shape
            && NumericsUtility.IsFinite(shape.Translation)
            && NumericsUtility.IsFinite(shape.Rotation)
            && NumericsUtility.IsFinite(shape.Scale)
            && NumericsUtility.IsFinite(shape.BoundsMin)
            && NumericsUtility.IsFinite(shape.BoundsMax)
            && shape.BoundsMin.X <= shape.BoundsMax.X
            && shape.BoundsMin.Y <= shape.BoundsMax.Y
            && shape.BoundsMin.Z <= shape.BoundsMax.Z;
    }
}

internal sealed class BgColliderDefinitionSet
{
    private readonly Dictionary<string, Dictionary<BgColliderDefinition, BgColliderDefinition>> _definitions = new(StringComparer.OrdinalIgnoreCase);

    public void Add(BgColliderDefinition definition)
    {
        if (!_definitions.TryGetValue(definition.ModelPath, out Dictionary<BgColliderDefinition, BgColliderDefinition>? variants))
        {
            variants = [];
            _definitions.Add(definition.ModelPath, variants);
        }

        BgColliderDefinition key = definition with
        {
            ModelPath = definition.ModelPath.ToLowerInvariant(),
            PcbPath = definition.PcbPath.ToLowerInvariant(),
            SourcePath = string.Empty,
        };

        if (!variants.TryGetValue(key, out BgColliderDefinition? previous)
         || StringComparer.Ordinal.Compare(definition.SourcePath, previous.SourcePath) < 0)
        {
            variants[key] = definition;
        }
    }

    public void AddRange(IEnumerable<BgColliderDefinition> definitions)
    {
        foreach (BgColliderDefinition definition in definitions)
        {
            Add(definition);
        }
    }

    public IReadOnlyList<BgColliderDefinition> GetDefinitions(string modelPath)
        => _definitions.TryGetValue(modelPath, out Dictionary<BgColliderDefinition, BgColliderDefinition>? variants)
            ? variants.Values.OrderBy(static p => p.Kind).ThenBy(static p => p.PcbPath, StringComparer.Ordinal)
                .ThenBy(static p => p.SourcePath, StringComparer.Ordinal).ToArray()
            : [];

    public IReadOnlyDictionary<string, IReadOnlyList<BgColliderDefinition>> Build()
        => _definitions.Keys.ToDictionary(static path => path, GetDefinitions, StringComparer.OrdinalIgnoreCase);
}
