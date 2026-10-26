using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using Intoner.Objects.Resources;
using Intoner.Objects.Models;
using System.Runtime.CompilerServices;

namespace Intoner.Objects.Assets;

internal static class BgColliderDefinitionReader
{
    public static BgColliderDefinition Read(LayoutFileReader reader, int offset, string modelPath)
    {
        FileLayerGroupInstanceBgPart bg = reader.Read<FileLayerGroupInstanceBgPart>(offset);
        BgCollisionKind kind;
        BgAnalyticShape? shape = null;
        string pcb = string.Empty;
        switch (bg.ColliderType)
        {
            case FileLayerGroupInstanceBgPart.Collider.None:
                kind = BgCollisionKind.None;
                break;
            case FileLayerGroupInstanceBgPart.Collider.Mesh:
                kind = BgCollisionKind.Mesh;
                pcb = ObjectResolvedPath.FromGamePath(reader.ReadOptionalString(offset, bg.OffsetPathPcb)).Path;
                break;
            case FileLayerGroupInstanceBgPart.Collider.Analytic:
                if (bg.OffsetColliderAnalyticData == 0)
                {
                    kind = BgCollisionKind.None;
                    break;
                }

                FileLayerGroupAnalyticCollider analytic = reader.Read<FileLayerGroupAnalyticCollider>(
                    reader.RelativeOffset(offset, bg.OffsetColliderAnalyticData, Unsafe.SizeOf<FileLayerGroupAnalyticCollider>(), allowNegative: true));
                kind = analytic.ColliderType switch
                {
                    FileLayerGroupAnalyticCollider.Type.None => BgCollisionKind.None,
                    FileLayerGroupAnalyticCollider.Type.Box => BgCollisionKind.Box,
                    FileLayerGroupAnalyticCollider.Type.Sphere => BgCollisionKind.Sphere,
                    FileLayerGroupAnalyticCollider.Type.Cylinder => BgCollisionKind.Cylinder,
                    FileLayerGroupAnalyticCollider.Type.Plane => BgCollisionKind.Plane,
                    _ => throw reader.Invalid("unsupported analytic collider"),
                };

                if (kind != BgCollisionKind.None)
                {
                    shape = new(analytic.Transform.Translation, analytic.Transform.Rotation, analytic.Transform.Scale,
                        analytic.Bounds.Min, analytic.Bounds.Max, analytic.MaterialId, analytic.MaterialMask);
                }

                break;
            default:
                throw reader.Invalid("unsupported BG collider");
        }

        BgColliderDefinition definition = new(modelPath, reader.FilePath, kind, pcb,
            ((ulong)bg.MaterialIdHigh << 32) | bg.MaterialIdLow,
            ((ulong)bg.MaterialMaskHigh << 32) | bg.MaterialMaskLow, shape);

        if (kind == BgCollisionKind.None)
        {
            definition = definition with { MaterialId = 0, MaterialMask = 0 };
        }

        return BgColliderDefinitionUtility.IsValid(definition, modelPath) ? definition : throw reader.Invalid("invalid BG collider data");
    }

    public static bool CollectSharedGroup(byte[] data, string sourcePath, BgColliderDefinitionSet? definitions,
        Action<string> collectPcb, CancellationToken cancellationToken)
    {
        bool complete = true;
        LayoutFileReader file = new(data, sourcePath);
        file.VisitSections((reader, magic) =>
        {
            if (magic == 0x314E4353)
            {
                reader.VisitSceneInstances(CollectInstance, cancellationToken);
            }
        }, cancellationToken);
        return complete;

        void CollectInstance(LayoutFileReader reader, int offset, FileLayerGroupInstance instance)
        {
            if (instance.Type != InstanceType.BgPart)
            {
                return;
            }

            try
            {
                FileLayerGroupInstanceBgPart bg = reader.Read<FileLayerGroupInstanceBgPart>(offset);

                if (bg.ColliderType == FileLayerGroupInstanceBgPart.Collider.Mesh)
                {
                    string pcbPath = ObjectResolvedPath.FromGamePath(reader.ReadOptionalString(offset, bg.OffsetPathPcb)).Path;

                    if (GameAssetPathRules.IsFileKind(pcbPath, GameAssetFileKind.Pcb))
                    {
                        collectPcb(pcbPath);
                    }
                    else
                    {
                        complete = false;
                    }
                }

                if (definitions is null)
                {
                    return;
                }

                string modelPath = ObjectResolvedPath.FromGamePath(reader.ReadOptionalString(offset, bg.OffsetPathMdl)).Path;

                if (ObjectAssetPathRules.IsCatalogModelPath(modelPath))
                {
                    definitions.Add(Read(reader, offset, modelPath));
                }
            }
            catch (InvalidDataException)
            {
                complete = false;
            }
        }
    }
}
