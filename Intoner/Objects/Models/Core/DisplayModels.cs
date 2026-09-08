using Intoner.Scene;
using Intoner.Services.Gpu;
using System.Numerics;

namespace Intoner.Displays;

internal sealed record DisplayDocument
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; init; } = CurrentFormatVersion;
    public IReadOnlyList<DisplaySnapshot> Displays { get; init; } = [];
}

internal sealed record DisplaySettings
{
    public static readonly Vector2 DefaultSize = new(1.6f, 0.9f);
    public static readonly Vector4 DefaultUvRect = new(0f, 0f, 1f, 1f);

    public WindowsCaptureTargetDescriptor Target { get; init; } = new();
    public Vector2 Size { get; init; } = DefaultSize;
    public Vector4 Tint { get; init; } = Vector4.One;
    public Vector4 UvRect { get; init; } = DefaultUvRect;
    public bool TwoSided { get; init; } = true;
    public bool UseSceneDepth { get; init; } = true;
    public bool NameplatesAboveDisplay { get; init; } = true;
}

internal sealed record DisplaySnapshot : SceneItemSnapshot
{
    public DisplaySettings Settings { get; init; } = new();
}
