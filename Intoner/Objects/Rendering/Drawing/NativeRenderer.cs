using Intoner.Objects.Rendering.Primitives;
using Intoner.Services.Configuration;

namespace Intoner.Objects.Rendering.Drawing;

internal sealed class NativeRenderer : IRenderer, IDisposable
{
    private readonly ImGuiRenderer               _imguiRenderer = new();
    private readonly PrimitiveService            _primitives;
    private readonly IIntonerConfigurationService _configurationService;
    private readonly PrimitiveCommandEncoder     _encoder = new();
    private readonly NativeTextTextureCache      _textTextures;

    private bool _disposed;

    public NativeRenderer(
        PrimitiveService primitives,
        IIntonerConfigurationService configurationService,
        NativeTextTextureCache textTextures)
    {
        _primitives            = primitives;
        _configurationService  = configurationService;
        _textTextures          = textTextures;
    }

    public void BeginFrame()
    {
        if (_disposed)
        {
            return;
        }

        _encoder.Clear();
        RenderingConfiguration rendering = ResolveRenderingConfiguration();
        if (rendering.DrawMode == DrawMode.ImGui)
        {
            _primitives.Deactivate();
            return;
        }

        _primitives.DeactivateIfIdle();
    }

    public void Draw(in DrawContext context, DrawBatch batch)
    {
        if (_disposed)
        {
            return;
        }

        RenderingConfiguration rendering = ResolveRenderingConfiguration();
        if (rendering.DrawMode == DrawMode.ImGui)
        {
            _imguiRenderer.Draw(context, batch);
            return;
        }

        bool nativeActive = TryCommitNativePrimitives(
            context,
            batch,
            new PrimitiveDrawState(rendering.DepthMode, rendering.AntiAliasing, rendering.DrawOverGameUi));
        if (!nativeActive && rendering.DrawMode == DrawMode.Automatic)
        {
            _imguiRenderer.Draw(context, batch);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _encoder.Clear();
        _primitives.Deactivate();
    }

    private RenderingConfiguration ResolveRenderingConfiguration()
        => _configurationService.Current.Rendering;

    private bool TryCommitNativePrimitives(in DrawContext context, DrawBatch batch, PrimitiveDrawState state)
    {
        if (!_encoder.Append(context, batch, _textTextures))
        {
            return false;
        }

        return _primitives.Commit(_encoder.Commands, state);
    }
}
