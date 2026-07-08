using Intoner.Objects.Assets;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Intoner.Objects.Preview.Rendering;

internal static class PreviewScene
{
    public const float NearPlane = 0.001f;

    private const float MinDirectionLengthSquared = 0.000001f;

    public static Frame CreateFrame(
        ModelPreviewGeometryReader.PreviewBounds bounds,
        PreviewRender.CameraState camera,
        int width,
        int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);

        Vector3 center = bounds.Center;
        float radius = bounds.Radius;
        float zoom = Math.Clamp(camera.Zoom, 0.65f, 2.40f);
        float yaw = camera.Yaw;
        float pitch = Math.Clamp(camera.Pitch, -1.25f, 1.25f);
        Vector3 orbit = new(
            MathF.Cos(pitch) * MathF.Sin(yaw),
            MathF.Sin(pitch),
            MathF.Cos(pitch) * MathF.Cos(yaw));
        orbit = ResolveDirection(orbit, new Vector3(-0.8f, 0.45f, 1f));

        float distance = MathF.Max(radius * ((2.2f * zoom) + 0.85f), radius + 0.1f);
        Vector3 cameraPosition = center + (orbit * distance);
        Matrix4x4 view = Matrix4x4.CreateLookAt(cameraPosition, center, Vector3.UnitY);
        float farPlane = distance + (radius * 6f) + 10f;
        Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 4.1f, width / (float)height, NearPlane, farPlane);
        (Vector3 backgroundTop, Vector3 backgroundBottom) = PreviewRender.BackgroundPalette.GetGradient(camera.BackgroundStyle);

        return new Frame(
            view,
            view * projection,
            cameraPosition,
            ResolveDirection(new Vector3(-0.45f, 0.75f, 0.35f), Vector3.UnitY),
            backgroundTop,
            backgroundBottom);
    }

    public static Vector3 ResolveDirection(Vector3 direction, Vector3 defaultDirection)
    {
        if (direction.LengthSquared() > MinDirectionLengthSquared)
        {
            return Vector3.Normalize(direction);
        }

        return defaultDirection.LengthSquared() > MinDirectionLengthSquared
            ? Vector3.Normalize(defaultDirection)
            : Vector3.UnitY;
    }

    [StructLayout(LayoutKind.Auto)]
    internal readonly record struct Frame(
        Matrix4x4 View,
        Matrix4x4 ViewProjection,
        Vector3 CameraPosition,
        Vector3 LightDirection,
        Vector3 BackgroundTop,
        Vector3 BackgroundBottom);
}
