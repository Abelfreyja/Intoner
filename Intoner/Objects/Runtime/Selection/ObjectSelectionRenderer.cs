using Intoner.Services.Gpu;
using SharpDX.D3DCompiler;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using System.Numerics;
using System.Runtime.InteropServices;
using Buffer = SharpDX.Direct3D11.Buffer;
using Device = SharpDX.Direct3D11.Device;

namespace Intoner.Objects.Runtime;

internal sealed class ObjectSelectionRenderer : IDisposable
{
    private const string ShaderResourceName = "Objects.Runtime.Selection.Shaders.ObjectSelection.hlsl";

    private static readonly Lazy<byte[]> VertexShaderBytecode = new(
        () => GpuShaderCompileService.CreateVertexShaderBytecode(
            typeof(ObjectSelectionRenderer),
            ShaderResourceName,
            "object selection vertex shader"));

    private static readonly Lazy<byte[]> PixelShaderBytecode = new(
        () => GpuShaderCompileService.CreatePixelShaderBytecode(
            typeof(ObjectSelectionRenderer),
            ShaderResourceName,
            "object selection pixel shader"));

    private readonly Device _device;
    private readonly VertexShader _vertexShader;
    private readonly PixelShader _pixelShader;
    private readonly InputLayout _inputLayout;
    private readonly RasterizerState _rasterizerState;
    private readonly DepthStencilState _forwardDepthStencilState;
    private readonly DepthStencilState _reverseDepthStencilState;
    private readonly ObjectSelectionGeometryCache _geometryCache;
    private readonly SelectionMeshCache _meshCache;
    private readonly SelectionViewportTargets _viewportTargets;

    public ObjectSelectionRenderer(Device device, ObjectSelectionGeometryCache geometryCache)
    {
        _device = device;
        _geometryCache = geometryCache;
        Context = device.ImmediateContext;
        _vertexShader = new VertexShader(device, VertexShaderBytecode.Value);
        _pixelShader = new PixelShader(device, PixelShaderBytecode.Value);
        _inputLayout = new InputLayout(
            device,
            ShaderSignature.GetInputSignature(VertexShaderBytecode.Value),
            [new InputElement("POSITION", 0, Format.R32G32B32_Float, 0, 0)]);
        ConstantBuffer = new Buffer(
            device,
            Marshal.SizeOf<ObjectSelectionConstants>(),
            ResourceUsage.Default,
            BindFlags.ConstantBuffer,
            CpuAccessFlags.None,
            ResourceOptionFlags.None,
            0);
        _rasterizerState = new RasterizerState(device, new RasterizerStateDescription
        {
            CullMode = CullMode.None,
            FillMode = FillMode.Solid,
            IsDepthClipEnabled = true,
            IsMultisampleEnabled = false,
            IsFrontCounterClockwise = false,
            IsScissorEnabled = false,
        });
        _forwardDepthStencilState = new DepthStencilState(device, new DepthStencilStateDescription
        {
            IsDepthEnabled = true,
            DepthComparison = Comparison.LessEqual,
            DepthWriteMask = DepthWriteMask.All,
            IsStencilEnabled = false,
        });
        _reverseDepthStencilState = new DepthStencilState(device, new DepthStencilStateDescription
        {
            IsDepthEnabled = true,
            DepthComparison = Comparison.GreaterEqual,
            DepthWriteMask = DepthWriteMask.All,
            IsStencilEnabled = false,
        });
        _meshCache = new SelectionMeshCache(device, geometryCache);
        _viewportTargets = new SelectionViewportTargets(device);
    }

    public DeviceContext Context { get; }
    public Buffer ConstantBuffer { get; }

    public bool TryRenderSelectionId(
        ObjectSelectionCollector collector,
        Matrix4x4 viewProjection,
        bool useReverseDepth,
        int viewportWidth,
        int viewportHeight,
        int pixelX,
        int pixelY,
        out uint selectionId)
    {
        selectionId = 0;

        _meshCache.TrimExpired();
        _viewportTargets.Ensure(viewportWidth, viewportHeight);
        PrepareFrame(viewportWidth, viewportHeight, useReverseDepth);
        DrawCollector(collector, viewProjection);
        return TryReadbackSelectionId(pixelX, pixelY, out selectionId);
    }

    public void TouchSelectionPaths(ObjectSelectionCollector collector, uint selectionId)
    {
        collector.TouchModelPaths(selectionId, modelPath =>
        {
            _geometryCache.Touch(modelPath);
            _meshCache.TouchModelMesh(modelPath);
        });
    }

    public void Dispose()
    {
        _viewportTargets.Dispose();
        _meshCache.Dispose();
        ConstantBuffer.Dispose();
        _reverseDepthStencilState.Dispose();
        _forwardDepthStencilState.Dispose();
        _rasterizerState.Dispose();
        _inputLayout.Dispose();
        _pixelShader.Dispose();
        _vertexShader.Dispose();
        Context.Dispose();
        _device.Dispose();
    }

    private void PrepareFrame(int width, int height, bool useReverseDepth)
    {
        Context.OutputMerger.SetTargets(_viewportTargets.DepthStencilView, _viewportTargets.RenderTargetView);
        Context.Rasterizer.SetViewport(0, 0, width, height);
        Context.Rasterizer.State = _rasterizerState;
        Context.OutputMerger.DepthStencilState = useReverseDepth ? _reverseDepthStencilState : _forwardDepthStencilState;
        Context.InputAssembler.InputLayout = _inputLayout;
        Context.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
        Context.VertexShader.Set(_vertexShader);
        Context.VertexShader.SetConstantBuffer(0, ConstantBuffer);
        Context.PixelShader.Set(_pixelShader);
        Context.ClearRenderTargetView(_viewportTargets.RenderTargetView, new SharpDX.Color4(0f, 0f, 0f, 0f));
        Context.ClearDepthStencilView(_viewportTargets.DepthStencilView, DepthStencilClearFlags.Depth, useReverseDepth ? 0f : 1f, 0);
    }

    private void DrawCollector(ObjectSelectionCollector collector, Matrix4x4 viewProjection)
    {
        foreach (var draw in collector.ModelDraws)
        {
            if (!_meshCache.TryGetModelMesh(draw.ModelPath, out var mesh))
            {
                continue;
            }

            DrawMesh(mesh, draw.SelectionId, draw.WorldTransform, viewProjection);
        }

        foreach (var draw in collector.PrimitiveDraws)
        {
            var mesh = _meshCache.GetPrimitiveMesh(draw.PrimitiveKind);
            DrawMesh(mesh, draw.SelectionId, draw.WorldTransform, viewProjection);
        }
    }

    private void DrawMesh(UploadedMesh mesh, uint selectionId, Matrix4x4 worldTransform, Matrix4x4 viewProjection)
    {
        var worldViewProjection = worldTransform * viewProjection;
        var constants = new ObjectSelectionConstants
        {
            WorldViewProjection = worldViewProjection,
            ObjectId = selectionId,
        };

        Context.UpdateSubresource(ref constants, ConstantBuffer);
        Context.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(mesh.VertexBuffer, Marshal.SizeOf<ObjectSelectionVertex>(), 0));
        Context.InputAssembler.SetIndexBuffer(mesh.IndexBuffer, Format.R32_UInt, 0);
        Context.DrawIndexed(mesh.IndexCount, 0, 0);
    }

    private bool TryReadbackSelectionId(int pixelX, int pixelY, out uint selectionId)
    {
        selectionId = 0;

        Context.CopySubresourceRegion(
            _viewportTargets.IdTexture,
            0,
            new ResourceRegion(pixelX, pixelY, 0, pixelX + 1, pixelY + 1, 1),
            _viewportTargets.ReadbackTexture,
            0,
            0,
            0,
            0);
        Context.Flush();

        var mapped = Context.MapSubresource(_viewportTargets.ReadbackTexture, 0, MapMode.Read, SharpDX.Direct3D11.MapFlags.None);
        try
        {
            selectionId = unchecked((uint)Marshal.ReadInt32(mapped.DataPointer));
            return true;
        }
        finally
        {
            Context.UnmapSubresource(_viewportTargets.ReadbackTexture, 0);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectSelectionVertex
    {
        public Vector3 Position;

        public ObjectSelectionVertex(Vector3 position)
        {
            Position = position;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectSelectionConstants
    {
        public Matrix4x4 WorldViewProjection;
        public uint ObjectId;
        public Vector3 Padding;
    }

    private sealed class UploadedMesh : IDisposable
    {
        public UploadedMesh(Buffer vertexBuffer, Buffer indexBuffer, int indexCount)
        {
            VertexBuffer = vertexBuffer;
            IndexBuffer = indexBuffer;
            IndexCount = indexCount;
        }

        public Buffer VertexBuffer { get; }
        public Buffer IndexBuffer { get; }
        public int IndexCount { get; }

        public void Dispose()
        {
            IndexBuffer.Dispose();
            VertexBuffer.Dispose();
        }
    }

    private sealed class SelectionMeshCache : IDisposable
    {
        private static readonly TimeSpan ModelMeshTtl = TimeSpan.FromMinutes(5);

        private sealed class ModelMeshEntry
        {
            public required UploadedMesh Mesh { get; init; }
            public DateTime LastTouchedUtc { get; set; }
        }

        private readonly Device _device;
        private readonly ObjectSelectionGeometryCache _geometryCache;
        private readonly Dictionary<string, ModelMeshEntry> _modelMeshes = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<ObjectSelectionPrimitiveKind, UploadedMesh> _primitiveMeshes = [];

        public SelectionMeshCache(Device device, ObjectSelectionGeometryCache geometryCache)
        {
            _device = device;
            _geometryCache = geometryCache;
        }

        public void TrimExpired()
        {
            var now = DateTime.UtcNow;
            foreach (var (modelPath, entry) in _modelMeshes.ToArray())
            {
                if (now - entry.LastTouchedUtc < ModelMeshTtl)
                {
                    continue;
                }

                entry.Mesh.Dispose();
                _modelMeshes.Remove(modelPath);
            }
        }

        public bool TryGetModelMesh(string modelPath, out UploadedMesh mesh)
        {
            if (_modelMeshes.TryGetValue(modelPath, out var cachedEntry))
            {
                mesh = cachedEntry.Mesh;
                return true;
            }

            if (!_geometryCache.TryGetGeometry(modelPath, out var geometry))
            {
                mesh = null!;
                return false;
            }

            mesh = UploadGeometry(_device, geometry);
            _modelMeshes[modelPath] = new ModelMeshEntry
            {
                Mesh = mesh,
                LastTouchedUtc = DateTime.UtcNow,
            };
            return true;
        }

        public void TouchModelMesh(string modelPath)
        {
            if (_modelMeshes.TryGetValue(modelPath, out var entry))
            {
                entry.LastTouchedUtc = DateTime.UtcNow;
            }
        }

        public UploadedMesh GetPrimitiveMesh(ObjectSelectionPrimitiveKind primitiveKind)
        {
            if (_primitiveMeshes.TryGetValue(primitiveKind, out var mesh))
            {
                return mesh;
            }

            mesh = UploadGeometry(_device, ObjectSelectionPrimitiveGeometry.Resolve(primitiveKind));
            _primitiveMeshes[primitiveKind] = mesh;
            return mesh;
        }

        public void Dispose()
        {
            foreach (var mesh in _primitiveMeshes.Values)
            {
                mesh.Dispose();
            }

            foreach (var entry in _modelMeshes.Values)
            {
                entry.Mesh.Dispose();
            }
        }

        private static UploadedMesh UploadGeometry(Device device, ObjectSelectionGeometry geometry)
        {
            var vertices = new ObjectSelectionVertex[geometry.Positions.Length];
            for (var index = 0; index < geometry.Positions.Length; index++)
            {
                vertices[index] = new ObjectSelectionVertex(geometry.Positions[index]);
            }

            var vertexBuffer = Buffer.Create(device, BindFlags.VertexBuffer, vertices);
            var indexData = new uint[geometry.Indices.Length];
            for (var index = 0; index < geometry.Indices.Length; index++)
            {
                indexData[index] = (uint)Math.Max(0, geometry.Indices[index]);
            }

            var indexBuffer = Buffer.Create(device, BindFlags.IndexBuffer, indexData);
            return new UploadedMesh(vertexBuffer, indexBuffer, indexData.Length);
        }
    }

    private sealed class SelectionViewportTargets : IDisposable
    {
        private readonly Device _device;

        private Texture2D? _idTexture;
        private RenderTargetView? _renderTargetView;
        private Texture2D? _depthTexture;
        private DepthStencilView? _depthStencilView;
        private Texture2D? _readbackTexture;
        private int _viewportWidth;
        private int _viewportHeight;

        public SelectionViewportTargets(Device device)
        {
            _device = device;
        }

        public Texture2D IdTexture => _idTexture!;
        public RenderTargetView RenderTargetView => _renderTargetView!;
        public DepthStencilView DepthStencilView => _depthStencilView!;
        public Texture2D ReadbackTexture => _readbackTexture!;

        public void Ensure(int width, int height)
        {
            if (_idTexture is not null && _viewportWidth == width && _viewportHeight == height)
            {
                return;
            }

            DisposeViewportTargets();

            _idTexture = new Texture2D(_device, new Texture2DDescription
            {
                Width = width,
                Height = height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.R32_UInt,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget,
                CpuAccessFlags = CpuAccessFlags.None,
                OptionFlags = ResourceOptionFlags.None,
            });
            _renderTargetView = new RenderTargetView(_device, _idTexture);

            _depthTexture = new Texture2D(_device, new Texture2DDescription
            {
                Width = width,
                Height = height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.D32_Float,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.DepthStencil,
                CpuAccessFlags = CpuAccessFlags.None,
                OptionFlags = ResourceOptionFlags.None,
            });
            _depthStencilView = new DepthStencilView(_device, _depthTexture);

            _readbackTexture = new Texture2D(_device, new Texture2DDescription
            {
                Width = 1,
                Height = 1,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.R32_UInt,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CpuAccessFlags = CpuAccessFlags.Read,
                OptionFlags = ResourceOptionFlags.None,
            });

            _viewportWidth = width;
            _viewportHeight = height;
        }

        public void Dispose()
            => DisposeViewportTargets();

        private void DisposeViewportTargets()
        {
            _readbackTexture?.Dispose();
            _readbackTexture = null;
            _depthStencilView?.Dispose();
            _depthStencilView = null;
            _depthTexture?.Dispose();
            _depthTexture = null;
            _renderTargetView?.Dispose();
            _renderTargetView = null;
            _idTexture?.Dispose();
            _idTexture = null;
            _viewportWidth = 0;
            _viewportHeight = 0;
        }
    }

}

