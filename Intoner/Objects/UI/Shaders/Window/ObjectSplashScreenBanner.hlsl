#include "ObjectWindowBackdropCommon.hlsli"

cbuffer BackdropSurfaceConstants : register(b0)
{
    float4 BackdropSize;  // x textureWidth, y textureHeight
    float4 RegionRect;    // x minX, y minY, z sizeX, w sizeY
    float4 CornerRadii;   // x topLeft, y topRight, z bottomRight, w bottomLeft
}

cbuffer SplashScreenBannerConstants : register(b1)
{
    float4 TimeParams;   // x time, y rate1, z rate2, w loopcycle
    float4 ColorParams;  // x color1, y color2, z cycle1, w cycle2
    float4 FlowParams;   // x nudge, y depthX, z depthY, w opacity
}

static const float Pi = 3.141592653589793f;

// based on glslsandbox.com/e#35553.0
// Creative Commons Attribution-NonCommercial-ShareAlike 3.0
float4 PSSplashScreenBanner(PSInput input) : SV_Target
{
    float2 pixel = input.Position.xy;
    float2 regionMin = RegionRect.xy;
    float2 regionSize = SafeSize(RegionRect.zw);
    float2 localPixel = pixel - regionMin;
    float2 localUv = saturate(localPixel / regionSize);

    float sdf = RoundedRectSdf(localPixel, regionSize, CornerRadii);
    float alpha = ShapeAlpha(sdf);
    if (alpha <= 1.0e-4f)
    {
        return float4(0.0f, 0.0f, 0.0f, 0.0f);
    }

    float time = TimeParams.x;
    float rate1 = TimeParams.y;
    float rate2 = TimeParams.z;
    float loopCycle = max(TimeParams.w, 1.0f);
    float color1 = ColorParams.x;
    float color2 = ColorParams.y;
    float cycle1 = ColorParams.z;
    float cycle2 = ColorParams.w;
    float nudge = FlowParams.x;
    float depthX = FlowParams.y;
    float depthY = FlowParams.z;
    float opacity = saturate(FlowParams.w);

    float t = time * rate1;
    float tt = time * rate2;
    float2 p = 2.0f * localUv;
    [unroll]
    for (int i = 1; i < 11; ++i)
    {
        float ii = (float)i;
        float2 nextP = p;
        nextP.x += depthX / ii * sin((ii * Pi * p.y) + (t * nudge) + cos((tt / (5.0f * ii)) * ii));
        nextP.y += depthY / ii * cos((ii * Pi * p.x) + tt + nudge + sin((t / (5.0f * ii)) * ii));
        p = nextP + log(max(time + 1.0f, 1.0f)) / loopCycle;
    }

    float field = p.x + p.y;
    float3 color = float3(
        cos(field + (3.0f * color1)) * 0.5f + 0.5f,
        sin(field + (6.0f * cycle1)) * 0.5f + 0.5f,
        (sin(field + (9.0f * color2)) + cos(field + (12.0f * cycle2))) * 0.25f + 0.5f);
    color *= color;

    float verticalShade = lerp(0.92f, 0.74f, smoothstep(0.0f, 1.0f, localUv.y));
    float edgeShade = 1.0f - (0.18f * (1.0f - smoothstep(0.0f, 0.20f, localUv.y)));
    color *= verticalShade * edgeShade;

    return float4(saturate(color), alpha * opacity);
}
