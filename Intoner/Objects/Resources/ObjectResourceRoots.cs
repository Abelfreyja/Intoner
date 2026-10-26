using Intoner.Objects.Models;
using System.Runtime.InteropServices;
using Intoner.Objects.Utils;

namespace Intoner.Objects.Resources;

internal readonly record struct ObjectResourceRoots(string Primary, string Collision)
{
    public static ObjectResourceRoots FromModel(ObjectData model)
        => new(ObjectSnapshotUtility.GetRootResourcePath(model),
            model is BgObjectModel { ColliderDefinition: { Kind: BgCollisionKind.Mesh } definition } ? definition.PcbPath : string.Empty);

    public override string ToString()
        => $"{Primary}|{Collision}";
}

[StructLayout(LayoutKind.Auto)]
internal readonly record struct ObjectResourceRevision(long Primary, long Collision);
