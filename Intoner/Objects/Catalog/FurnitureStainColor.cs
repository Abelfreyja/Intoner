using FFXIVClientStructs.FFXIV.Client.Graphics;
using System.Runtime.InteropServices;

namespace Intoner.Objects.Catalog;

[StructLayout(LayoutKind.Auto)]
internal readonly record struct FurnitureStainColor(byte StainId, ByteColor Color);
