using FFXIVClientStructs.FFXIV.Client.Graphics;
using System.Runtime.InteropServices;

namespace Intoner.Objects.Models;

[StructLayout(LayoutKind.Auto)]
internal readonly record struct SheetFurnitureStainOption(string Name, ByteColor Color, bool IsMetallic);
