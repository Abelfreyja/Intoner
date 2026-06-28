using Dalamud.Bindings.ImGui;
using Intoner.Services.Gpu;
using SharpDX.Direct3D11;
using System.Numerics;
using System.Runtime.InteropServices;
using Buffer = SharpDX.Direct3D11.Buffer;

namespace Intoner.Objects.UI.Services.Backdrop;

/// <summary> base for window backdrop effects </summary>
internal abstract class BackdropEffectBase
{
    protected BackdropEffectBase(BackdropRenderer renderer)
    {
        Renderer = renderer;
    }

    protected BackdropRenderer Renderer { get; }

    /// <summary> releases effect owned runtime resources </summary>
    internal virtual void DisposeResources()
    { }
}

internal abstract class PreparedBackdropEffectBase<TStyle, TRequest> : BackdropEffectBase
    where TRequest : struct
{
    protected PreparedBackdropEffectBase(BackdropRenderer renderer)
        : base(renderer)
    { }

    /// <summary> tint used when drawing the prepared texture back into imgui </summary>
    protected virtual uint ResolveDrawTint(in TRequest request)
        => 0xFFFFFFFFu;

    /// <summary> builds an effect request for a prepared backdrop region </summary>
    protected abstract TRequest CreateRequest(in BackdropRenderer.BackdropRegion region, TStyle style);

    /// <summary> resolves texture draw info for a prepared effect request </summary>
    protected abstract bool TryResolveDraw(in TRequest request, out BackdropRenderer.TextureDrawInfo draw);

    /// <summary> draws the prepared effect output over a rounded region </summary>
    public void DrawRegion(
        ImDrawListPtr drawList,
        Vector2 min,
        Vector2 max,
        float rounding,
        TStyle style,
        ImDrawFlags roundingFlags = ImDrawFlags.RoundCornersAll)
    {
        if (!Renderer.TryPrepareBackdropRegion(drawList, min, max, rounding, roundingFlags, out var region))
        {
            return;
        }

        var request = CreateRequest(region, style);
        if (!TryResolveDraw(request, out var draw))
        {
            return;
        }

        drawList.AddImageRounded(
            new ImTextureID(draw.TextureHandle),
            min,
            max,
            draw.UvMin,
            draw.UvMax,
            ResolveDrawTint(request),
            rounding,
            roundingFlags);
    }
}

/// <summary> stores registered backdrop effects and provides typed lookup </summary>
internal sealed class BackdropEffectRegistry
{
    private readonly BackdropRenderer _renderer;
    private readonly Lock _lock = new();
    private readonly Dictionary<Type, Func<BackdropRenderer, BackdropEffectBase>> _factories = [];
    private readonly Dictionary<Type, BackdropEffectBase> _effects = [];

    public BackdropEffectRegistry(BackdropRenderer renderer)
    {
        _renderer = renderer;
    }

    /// <summary> registers a lazy factory for a backdrop effect type and resets any cached instance for that type </summary>
    public BackdropEffectRegistry Register<T>(Func<BackdropRenderer, T> factory)
        where T : BackdropEffectBase
    {
        return Register(typeof(T), renderer => factory(renderer));
    }

    internal BackdropEffectRegistry Register(Type effectType, Func<BackdropRenderer, BackdropEffectBase> factory)
    {
        BackdropEffectBase? existingEffect = null;
        lock (_lock)
        {
            _factories[effectType] = factory;
            if (_effects.Remove(effectType, out var cachedEffect))
            {
                existingEffect = cachedEffect;
            }
        }

        existingEffect?.DisposeResources();
        return this;
    }

    /// <summary> resolves a backdrop effect instance, creating it on first use </summary>
    public T Get<T>()
        where T : BackdropEffectBase
    {
        var effectType = typeof(T);
        lock (_lock)
        {
            if (_effects.TryGetValue(effectType, out var effect) && effect is T typedEffect)
            {
                return typedEffect;
            }

            if (_factories.TryGetValue(effectType, out var factory))
            {
                var createdEffect = factory(_renderer);
                _effects[effectType] = createdEffect;
                return (T)createdEffect;
            }
        }

        throw new InvalidOperationException($"backdrop effect '{effectType.Name}' is not registered");
    }

    /// <summary> disposes all created backdrop effect resources </summary>
    public void DisposeResources()
    {
        BackdropEffectBase[] effects;
        lock (_lock)
        {
            effects = [.. _effects.Values];
        }

        foreach (var effect in effects)
        {
            effect.DisposeResources();
        }
    }
}

/// <summary> stores backdrop effect factories that should be applied to new renderers </summary>
internal sealed class BackdropEffectRegistrationService
{
    private readonly Lock _lock = new();
    private readonly Dictionary<Type, Func<BackdropRenderer, BackdropEffectBase>> _factories = [];
    private bool _isFrozen;

    /// <summary> registers a backdrop effect factory for future renderer instances before the first registry snapshot is created </summary>
    public BackdropEffectRegistrationService Register<T>(Func<BackdropRenderer, T> factory)
        where T : BackdropEffectBase
    {
        lock (_lock)
        {
            if (_isFrozen)
            {
                throw new InvalidOperationException("backdrop effect registration is closed after the first renderer is created");
            }

            _factories[typeof(T)] = renderer => factory(renderer);
        }

        return this;
    }

    /// <summary> creates a runtime effect registry for a renderer from the registered factories </summary>
    public BackdropEffectRegistry CreateRegistry(BackdropRenderer renderer)
    {
        KeyValuePair<Type, Func<BackdropRenderer, BackdropEffectBase>>[] factories;
        lock (_lock)
        {
            _isFrozen = true;
            factories = [.. _factories];
        }

        var registry = new BackdropEffectRegistry(renderer);
        foreach (var (effectType, factory) in factories)
        {
            registry.Register(effectType, factory);
        }

        return registry;
    }
}

internal readonly struct BlurRequest
{
    public BlurRequest(BackdropRenderer.BackdropRegion region, uint tintColor)
    {
        Region = region;
        TintColor = tintColor;
    }

    public BackdropRenderer.BackdropRegion Region { get; }
    public uint TintColor { get; }
}

/// <summary> shared contract for shader backed object window backdrop effects </summary>
internal abstract unsafe class ShaderBackdropEffectBase<TStyle, TRequest, TEffectConstants> : BackdropEffectBase
    where TRequest : struct
    where TEffectConstants : unmanaged
{
    private PixelShader? _shader;
    private BackdropRenderer.BackdropFramebuffer? _framebuffer;
    private Buffer? _effectConstantBuffer;
    private int _clearedFrame = -1;

    protected ShaderBackdropEffectBase(BackdropRenderer renderer)
        : base(renderer)
    { }

    /// <summary> embedded shader resource used for this effect </summary>
    protected abstract Lazy<byte[]> ShaderBytecode { get; }

    /// <summary> log message used when effect processing fails </summary>
    protected abstract string FailureLogMessage { get; }

    /// <summary> tint used when drawing the effect framebuffer back into imgui </summary>
    protected virtual uint DrawTintColor => 0xFFFFFFFFu;

    /// <summary> builds an effect request for a prepared backdrop region </summary>
    protected abstract TRequest CreateRequest(in BackdropRenderer.BackdropRegion region, TStyle style);

    /// <summary> resolves the prepared region for an effect request </summary>
    protected abstract BackdropRenderer.BackdropRegion GetRegion(in TRequest request);

    /// <summary> packs effect specific constants for an effect request </summary>
    protected abstract TEffectConstants CreateEffectConstants(in TRequest request);

    /// <summary> draws the effect output over a prepared rounded region </summary>
    public void DrawRegion(
        ImDrawListPtr drawList,
        Vector2 min,
        Vector2 max,
        float rounding,
        TStyle style,
        ImDrawFlags roundingFlags = ImDrawFlags.RoundCornersAll)
    {
        if (!TryPrepareShaderDraw(drawList, min, max, rounding, roundingFlags, out var region, out var draw))
        {
            return;
        }

        var request = CreateRequest(region, style);
        Renderer.QueueEffectCallback(drawList, ProcessEffectCallback, new ShaderCallbackData(this, request));
        drawList.AddImageRounded(
            new ImTextureID(draw.TextureHandle),
            min,
            max,
            draw.UvMin,
            draw.UvMax,
            DrawTintColor,
            rounding,
            roundingFlags);
    }

    protected bool TryRenderShader(in BackdropRenderer.BackdropRegion region, in BackdropRenderer.BackdropSurfaceConstants constants)
    {
        if (_shader is null
            || _framebuffer is null
            || _effectConstantBuffer is null)
        {
            return false;
        }

        return Renderer.TryRenderEffect(
            _shader,
            _framebuffer,
            constants,
            _effectConstantBuffer,
            region.ScissorMinX,
            region.ScissorMinY,
            region.ScissorMaxX,
            region.ScissorMaxY,
            ref _clearedFrame,
            FailureLogMessage);
    }

    internal override void DisposeResources()
    {
        _effectConstantBuffer?.Dispose();
        _effectConstantBuffer = null;
        _framebuffer?.Dispose();
        _framebuffer = null;
        _shader?.Dispose();
        _shader = null;
        _clearedFrame = -1;
    }

    private bool TryPrepareShaderDraw(
        ImDrawListPtr drawList,
        Vector2 min,
        Vector2 max,
        float rounding,
        ImDrawFlags roundingFlags,
        out BackdropRenderer.BackdropRegion region,
        out BackdropRenderer.TextureDrawInfo draw)
    {
        draw = default;
        if (!Renderer.TryPrepareBackdropRegion(drawList, min, max, rounding, roundingFlags, out region)
            || !Renderer.TryEnsureEffectFramebuffer(ref _framebuffer)
            || !Renderer.TryEnsureEffectShader(ShaderBytecode, ref _shader)
            || !Renderer.TryEnsureEffectConstantBuffer<TEffectConstants>(ref _effectConstantBuffer)
            || _framebuffer?.ShaderResourceView is null)
        {
            return false;
        }

        draw = new BackdropRenderer.TextureDrawInfo(
            _framebuffer.ShaderResourceView.NativePointer,
            region.UvMin,
            region.UvMax);
        return true;
    }

    private static unsafe void ProcessEffectCallback(ImDrawList* _, ImDrawCmd* cmd)
    {
        var handle = GCHandle.FromIntPtr((nint)cmd->UserCallbackData);
        try
        {
            if (handle.Target is ShaderCallbackData callbackData)
            {
                callbackData.Effect.ProcessRequest(callbackData.Request);
            }
        }
        finally
        {
            handle.Free();
        }
    }

    private sealed class ShaderCallbackData
    {
        public ShaderCallbackData(ShaderBackdropEffectBase<TStyle, TRequest, TEffectConstants> effect, TRequest request)
        {
            Effect = effect;
            Request = request;
        }

        public ShaderBackdropEffectBase<TStyle, TRequest, TEffectConstants> Effect { get; }
        public TRequest Request { get; }
    }

    private void ProcessRequest(in TRequest request)
    {
        var region = GetRegion(request);
        var surfaceConstants = Renderer.CreateSurfaceConstants(region);
        var effectConstants = CreateEffectConstants(request);
        if (_effectConstantBuffer is null || !Renderer.TryUpdateConstantBuffer(_effectConstantBuffer, effectConstants))
        {
            return;
        }

        TryRenderShader(region, surfaceConstants);
    }
}

/// <summary> draws blurred backdrop regions </summary>
internal sealed class BlurEffect
    : PreparedBackdropEffectBase<BlurEffect.Style, BlurRequest>
{
    internal readonly struct Style
    {
        public Style()
        {
            TintColor = 0xFFFFFFFFu;
        }

        public uint TintColor { get; init; }
    }

    public BlurEffect(BackdropRenderer renderer)
        : base(renderer)
    { }

    protected override uint ResolveDrawTint(in BlurRequest request)
        => request.TintColor;

    protected override BlurRequest CreateRequest(in BackdropRenderer.BackdropRegion region, Style style)
        => new(region, style.TintColor);

    protected override bool TryResolveDraw(in BlurRequest request, out BackdropRenderer.TextureDrawInfo draw)
        => Renderer.TryCreateBlurDraw(request.Region, out draw);

    public void DrawRegion(
        ImDrawListPtr drawList,
        Vector2 min,
        Vector2 max,
        float rounding,
        uint tintColor,
        ImDrawFlags roundingFlags = ImDrawFlags.RoundCornersAll)
        => DrawRegion(drawList, min, max, rounding, new Style { TintColor = tintColor }, roundingFlags);
}

internal readonly struct GlassRequest
{
    public GlassRequest(BackdropRenderer.BackdropRegion region, GlassEffect.Style style)
    {
        Region = region;
        TintColor = style.TintColor;
        EdgeColor = style.EdgeColor;
        Params0 = style.PackParams0();
        Params1 = style.PackParams1();
    }

    public BackdropRenderer.BackdropRegion Region { get; }
    public Vector4 TintColor { get; }
    public Vector4 EdgeColor { get; }
    public Vector4 Params0 { get; }
    public Vector4 Params1 { get; }
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct GlassConstants
{
    public GlassConstants(Vector4 tintColor, Vector4 edgeColor, Vector4 glassParams0, Vector4 glassParams1)
    {
        TintColor = tintColor;
        EdgeColor = edgeColor;
        GlassParams0 = glassParams0;
        GlassParams1 = glassParams1;
    }

    public Vector4 TintColor { get; }
    public Vector4 EdgeColor { get; }
    public Vector4 GlassParams0 { get; }
    public Vector4 GlassParams1 { get; }
}

/// <summary> draws glass regions </summary>
internal sealed class GlassEffect
    : ShaderBackdropEffectBase<GlassEffect.Style, GlassRequest, GlassConstants>
{
    private const string GlassShaderResourceName = "Objects.UI.Shaders.Window.ObjectWindowGlass.hlsl";

    private static readonly Lazy<byte[]> GlassShaderBytecode = new(
        () => GpuShaderCompileService.CreatePixelShaderBytecode(
            typeof(BackdropRenderer),
            GlassShaderResourceName,
            "object window glass shader",
            "PSGlass"));

    internal readonly struct Style
    {
        public Style()
        {
            TintColor = new Vector4(0.16f, 0.19f, 0.24f, 0.38f);
            EdgeColor = new Vector4(0.92f, 0.96f, 1.00f, 0.58f);
            BlurMix = 0.84f;
            DistortionStrength = 8f;
            HighlightStrength = 0.78f;
            FrostStrength = 0.28f;
            NoiseAmount = 0.012f;
            ShadowStrength = 0.16f;
            EdgeBand = 18f;
            ChromaticAberration = 1.1f;
        }

        public Vector4 TintColor { get; init; }
        public Vector4 EdgeColor { get; init; }
        public float BlurMix { get; init; }
        public float DistortionStrength { get; init; }
        public float HighlightStrength { get; init; }
        public float FrostStrength { get; init; }
        public float NoiseAmount { get; init; }
        public float ShadowStrength { get; init; }
        public float EdgeBand { get; init; }
        public float ChromaticAberration { get; init; }

        public readonly Vector4 PackParams0()
            => new(BlurMix, DistortionStrength, HighlightStrength, FrostStrength);

        public readonly Vector4 PackParams1()
            => new(NoiseAmount, ShadowStrength, EdgeBand, ChromaticAberration);
    }

    public GlassEffect(BackdropRenderer renderer)
        : base(renderer)
    { }

    protected override Lazy<byte[]> ShaderBytecode => GlassShaderBytecode;
    protected override string FailureLogMessage => "object window glass processing failed";

    protected override GlassRequest CreateRequest(in BackdropRenderer.BackdropRegion region, Style style)
        => new(region, style);

    protected override BackdropRenderer.BackdropRegion GetRegion(in GlassRequest request)
        => request.Region;

    protected override GlassConstants CreateEffectConstants(in GlassRequest request)
        => new(request.TintColor, request.EdgeColor, request.Params0, request.Params1);
}

