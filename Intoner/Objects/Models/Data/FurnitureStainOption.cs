using System.Numerics;
using System.Runtime.InteropServices;

namespace Intoner.Objects.Models;

[StructLayout(LayoutKind.Auto)]
internal readonly record struct FurnitureStainOption(byte Id, string Name, Vector4 PreviewColor, bool IsMetallic);
