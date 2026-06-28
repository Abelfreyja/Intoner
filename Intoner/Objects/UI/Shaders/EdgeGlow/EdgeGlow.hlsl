cbuffer EdgeGlowConstants : register(b0)
{
    float2 TextureSize;
    float2 Padding0;
    float4 OuterRadii;
    float4 InnerRadii;
    float4 BeamMotion0;  // line: x beamX, y beamW, z beamH, w beamOpacity; full: x angle01, w beamOpacity
    float4 BeamMotion1;  // line: x beamEdge, y beamSpike, z beamSpike2, w strength; full: w strength
    float4 ColorParams0; // x hueShift, y bloomHueShift, z brightness, w saturation
    float4 ColorParams1; // x strokeOpacity, y innerOpacity, z bloomOpacity, w innerShadowAlpha
    float4 LayoutParams0; // x borderWidthPx, y scale
    float4 StateParams; // x themeDark, y applyBaseColorTransform, z applyBloomColorTransform
    float4 LineParams; // x innerBandPx, y innerShadowBlurPx, z horizontalFootprintScale
    float4 FullBorderParams; // x innerBandPx, y innerShadowBlurPx, z innerReachScale, w sweepScale
    float4 StrokeSpots[9];
    float4 StrokeColors[9];
    float4 InnerSpots[9];
    float4 InnerColors[9];
    float4 BloomColors[14];
}

cbuffer EdgeGlowBlurConstants : register(b1)
{
    float2 HalfPixel;
    float Offset;
    float BlurPadding0;
}

Texture2D EdgeGlowTexture : register(t0);
SamplerState EdgeGlowSampler : register(s0);

struct VSInput
{
    float2 Position : POSITION;
    float2 Uv       : TEXCOORD0;
};

struct PSInput
{
    float4 Position : SV_POSITION;
    float2 Uv       : TEXCOORD0;
};

struct BorderMaskData
{
    float OuterSdf;
    float OuterAlpha;
    float RingMask;
    float InnerEdgeMask;
};

static const int SpotCount = 9;
static const float Tau = 6.28318530718f;
static const float Pi = 3.14159265359f;

#include "EdgeGlowCommon.hlsli"
#include "EdgeGlowLine.hlsli"
#include "EdgeGlowFullBorder.hlsli"

PSInput VSMain(VSInput input)
{
    PSInput output;
    output.Position = float4(input.Position, 0.0f, 1.0f);
    output.Uv = input.Uv;
    return output;
}

float4 BuildOutput(float4 strokeLayer, float4 innerLayer)
{
    float4 color = float4(0.0f, 0.0f, 0.0f, 0.0f);
    color = CompositeOver(color, innerLayer);
    color = CompositeOver(color, strokeLayer);
    return saturate(color);
}

float4 PSLineMain(PSInput input) : SV_TARGET
{
    float2 pixel = input.Uv * TextureSize;
    bool themeDark = StateParams.x > 0.5f;
    bool renderStroke = ColorParams1.x > 0.0f;
    bool renderInner = ColorParams1.y > 0.0f;
    if (!renderStroke && !renderInner)
    {
        return float4(0.0f, 0.0f, 0.0f, 0.0f);
    }

    BorderMaskData mask = ComputeBorderMask(pixel, false);
    if (mask.OuterAlpha <= 0.0f)
    {
        return float4(0.0f, 0.0f, 0.0f, 0.0f);
    }

    float strength = BeamMotion1.w;
    if (strength <= 0.0f || BeamMotion0.w <= 0.0f)
    {
        return float4(0.0f, 0.0f, 0.0f, 0.0f);
    }

    float scale = LayoutParams0.y;
    float beamEdge = BeamMotion1.x;
    float beamW = BeamMotion0.y;
    float beamH = BeamMotion0.z;
    float2 beamCenter = float2(BeamMotion0.x * TextureSize.x, TextureSize.y);

    float beamMask = SampleLineBeamMask(pixel, beamCenter, beamW, beamH, scale);
    float baseMaskAlpha = beamEdge * beamMask;
    float strokeMaskAlpha = renderStroke ? baseMaskAlpha : 0.0f;
    float innerMaskAlpha = renderInner ? (baseMaskAlpha * mask.InnerEdgeMask) : 0.0f;
    if (((strokeMaskAlpha <= 0.0f) || (mask.RingMask <= 0.0f)) && innerMaskAlpha <= 0.0f)
    {
        return float4(0.0f, 0.0f, 0.0f, 0.0f);
    }

    float4 strokeLayer = float4(0.0f, 0.0f, 0.0f, 0.0f);
    if (renderStroke && mask.RingMask > 0.0f && strokeMaskAlpha > 0.0f)
    {
        float4 strokeSource = CompositeOver(
            SampleStrokeSpotField(pixel, beamCenter, float2(beamW, beamH)),
            SampleLineWhiteHighlight(pixel, beamCenter, beamW, beamH, scale, themeDark));
        strokeSource.rgb = ApplyColorTransformIfNeeded(strokeSource.rgb, ColorParams0.x, ColorParams0.w, ColorParams0.z, StateParams.y > 0.5f);
        strokeLayer = ComposeStrokeLayer(strokeSource, strokeMaskAlpha, mask.RingMask, strength);
    }

    float4 innerLayer = float4(0.0f, 0.0f, 0.0f, 0.0f);
    if (renderInner && innerMaskAlpha > 0.0f)
    {
        innerLayer = ComposeInnerLayer(
            SampleInnerSpotField(pixel, beamCenter, float2(beamW, beamH)),
            mask.OuterSdf,
            mask.OuterAlpha,
            innerMaskAlpha,
            LineParams.y,
            themeDark,
            strength);
    }

    return BuildOutput(strokeLayer, innerLayer);
}

float4 PSLineBloomOnly(PSInput input) : SV_TARGET
{
    if (ColorParams1.z <= 0.0f || BeamMotion0.w <= 0.0f || BeamMotion1.w <= 0.0f || BeamMotion1.x <= 0.0f)
    {
        return float4(0.0f, 0.0f, 0.0f, 0.0f);
    }

    float2 pixel = input.Uv * TextureSize;
    bool themeDark = StateParams.x > 0.5f;
    BorderMaskData mask = ComputeBorderMask(pixel, false);
    if (mask.OuterAlpha <= 0.0f)
    {
        return float4(0.0f, 0.0f, 0.0f, 0.0f);
    }

    return saturate(ComposeLineBloomLayer(pixel, mask.OuterAlpha, themeDark, BeamMotion1.w));
}

float4 PSFullMain(PSInput input) : SV_TARGET
{
    float2 pixel = input.Uv * TextureSize;
    bool themeDark = StateParams.x > 0.5f;
    bool renderStroke = ColorParams1.x > 0.0f;
    bool renderInner = ColorParams1.y > 0.0f;
    if (!renderStroke && !renderInner)
    {
        return float4(0.0f, 0.0f, 0.0f, 0.0f);
    }

    BorderMaskData mask = ComputeBorderMask(pixel, true);
    if (mask.OuterAlpha <= 0.0f)
    {
        return float4(0.0f, 0.0f, 0.0f, 0.0f);
    }

    float strength = BeamMotion1.w;
    if (strength <= 0.0f || BeamMotion0.w <= 0.0f)
    {
        return float4(0.0f, 0.0f, 0.0f, 0.0f);
    }

    float angle01 = NormalizedConicAngle01(pixel);
    float fullMask = SampleFullStrokeMask(angle01);
    float strokeMaskAlpha = renderStroke ? fullMask : 0.0f;
    float innerMaskAlpha = renderInner ? (fullMask * mask.InnerEdgeMask) : 0.0f;
    if (((strokeMaskAlpha <= 0.0f) || (mask.RingMask <= 0.0f)) && innerMaskAlpha <= 0.0f)
    {
        return float4(0.0f, 0.0f, 0.0f, 0.0f);
    }

    float4 strokeLayer = float4(0.0f, 0.0f, 0.0f, 0.0f);
    if (renderStroke && mask.RingMask > 0.0f && strokeMaskAlpha > 0.0f)
    {
        float4 strokeSource = CompositeOver(
            SampleStrokeSpotField(pixel, float2(0.0f, 0.0f), float2(1.0f, 1.0f)),
            SampleFullWhiteHighlight(angle01, themeDark));
        strokeSource.rgb = ApplyColorTransformIfNeeded(strokeSource.rgb, ColorParams0.x, ColorParams0.w, ColorParams0.z, StateParams.y > 0.5f);
        strokeLayer = ComposeStrokeLayer(strokeSource, strokeMaskAlpha, mask.RingMask, strength);
    }

    float4 innerLayer = float4(0.0f, 0.0f, 0.0f, 0.0f);
    if (renderInner && innerMaskAlpha > 0.0f)
    {
        innerLayer = ComposeInnerLayer(
            SampleInnerSpotField(pixel, float2(0.0f, 0.0f), float2(1.0f, 1.0f)),
            mask.OuterSdf,
            mask.OuterAlpha,
            innerMaskAlpha,
            FullBorderParams.y,
            themeDark,
            strength);
    }

    return BuildOutput(strokeLayer, innerLayer);
}

float4 PSFullBloomOnly(PSInput input) : SV_TARGET
{
    if (ColorParams1.z <= 0.0f || BeamMotion0.w <= 0.0f || BeamMotion1.w <= 0.0f)
    {
        return float4(0.0f, 0.0f, 0.0f, 0.0f);
    }

    float2 pixel = input.Uv * TextureSize;
    bool themeDark = StateParams.x > 0.5f;
    BorderMaskData mask = ComputeBorderMask(pixel, true);
    if (mask.OuterAlpha <= 0.0f || mask.RingMask <= 0.0f)
    {
        return float4(0.0f, 0.0f, 0.0f, 0.0f);
    }

    return saturate(ComposeFullBloomLayer(pixel, mask.RingMask, themeDark, BeamMotion1.w));
}

float4 PSDownsample(PSInput input) : SV_TARGET
{
    float2 delta = HalfPixel * Offset;
    float4 sum = SampleBeamTexturePremultiplied(input.Uv) * 4.0f;
    sum += SampleBeamTexturePremultiplied(input.Uv - delta);
    sum += SampleBeamTexturePremultiplied(input.Uv + delta);
    sum += SampleBeamTexturePremultiplied(input.Uv + float2(delta.x, -delta.y));
    sum += SampleBeamTexturePremultiplied(input.Uv - float2(delta.x, -delta.y));
    return ResolvePremultiplied(sum / 8.0f);
}

float4 PSUpsample(PSInput input) : SV_TARGET
{
    float2 delta = HalfPixel * Offset;
    float4 sum = SampleBeamTexturePremultiplied(input.Uv + float2(-delta.x * 2.0f, 0.0f));
    sum += SampleBeamTexturePremultiplied(input.Uv + float2(-delta.x, delta.y)) * 2.0f;
    sum += SampleBeamTexturePremultiplied(input.Uv + float2(0.0f, delta.y * 2.0f));
    sum += SampleBeamTexturePremultiplied(input.Uv + float2(delta.x, delta.y)) * 2.0f;
    sum += SampleBeamTexturePremultiplied(input.Uv + float2(delta.x * 2.0f, 0.0f));
    sum += SampleBeamTexturePremultiplied(input.Uv + float2(delta.x, -delta.y)) * 2.0f;
    sum += SampleBeamTexturePremultiplied(input.Uv + float2(0.0f, -delta.y * 2.0f));
    sum += SampleBeamTexturePremultiplied(input.Uv + float2(-delta.x, -delta.y)) * 2.0f;
    return ResolvePremultiplied(sum / 12.0f);
}
