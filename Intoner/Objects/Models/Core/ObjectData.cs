using System;
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
internal abstract record ObjectData;

