using Intoner.Services.Gpu;
using SharpDX.Direct3D11;
using System.Numerics;
using System.Runtime.InteropServices;
using Buffer = SharpDX.Direct3D11.Buffer;

namespace Intoner.Objects.Preview.Rendering;

internal sealed class ViewportMesh : IDisposable
{
    private readonly IReadOnlyList<GpuTextureView>   _ownedTextures;
    private readonly GpuLeasedResource<ViewportMesh> _lifetime;

    public static int VertexStride { get; } = Marshal.SizeOf<Vertex>();

    public ViewportMesh(
        Buffer vertexBuffer,
        IReadOnlyList<DrawRange> opaqueRanges,
        IReadOnlyList<DrawRange> transparentRanges,
        IReadOnlyList<GpuTextureView> ownedTextures)
    {
        VertexBuffer       = vertexBuffer;
        OpaqueRanges       = opaqueRanges;
        TransparentRanges  = transparentRanges;
        _ownedTextures     = ownedTextures;
        _lifetime          = new GpuLeasedResource<ViewportMesh>(this, static mesh => mesh.DisposeResources());
    }

    public Buffer VertexBuffer { get; }
    public IReadOnlyList<DrawRange> OpaqueRanges { get; }
    public IReadOnlyList<DrawRange> TransparentRanges { get; }

    public GpuLeasedResource<ViewportMesh>.Lease Acquire()
        => _lifetime.Acquire();

    public void Dispose()
        => _lifetime.Dispose();

    private void DisposeResources()
    {
        foreach (GpuTextureView texture in _ownedTextures)
        {
            texture.Dispose();
        }

        VertexBuffer.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct Vertex
    {
        public readonly Vector3 Position;
        public readonly Vector3 Normal;
        public readonly Vector2 TexCoord;

        public Vertex(Vector3 position, Vector3 normal, Vector2 texCoord)
        {
            Position = position;
            Normal   = normal;
            TexCoord = texCoord;
        }
    }

    [StructLayout(LayoutKind.Auto)]
    internal readonly record struct DrawRange(
        int StartVertex,
        int VertexCount,
        ShaderResourceView DiffuseView,
        bool HasDiffuseTexture,
        bool ApplyAlphaClip,
        float Transparency,
        Vector3 UntexturedDiffuseColor)
    {
        public ViewportResources.MaterialConstants CreateConstants()
            => new()
            {
                UntexturedDiffuseColor = new Vector4(UntexturedDiffuseColor, 1f),
                MaterialParams = new Vector4(
                    HasDiffuseTexture ? 1f : 0f,
                    ApplyAlphaClip ? 1f : 0f,
                    Math.Clamp(Transparency, 0f, 1f),
                    0f),
            };
    }
}
