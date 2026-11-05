using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace Intoner.Objects.Models;

[Flags]
internal enum ObjectKind
{
    Light = 1,
    BgObject = 2,
    Furniture = 4,
    Vfx = 8,
}

[
    JsonPolymorphic(TypeDiscriminatorPropertyName = "$type"),
    JsonDerivedType(typeof(LightModel), typeDiscriminator: "light"),
    JsonDerivedType(typeof(BgObjectModel), typeDiscriminator: "bgobject"),
    JsonDerivedType(typeof(FurnitureModel), typeDiscriminator: "furniture"),
    JsonDerivedType(typeof(VfxModel), typeDiscriminator: "vfx"),
]
[SuppressMessage("Minor Code Smell", "S1694", Justification = "object models share record equality and a polymorphic JSON contract")]
internal abstract record ObjectData
{
    internal abstract ObjectKind Kind { get; }
}
