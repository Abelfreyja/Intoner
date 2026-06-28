#include "ObjectWindowBackdropCommon.hlsli"

cbuffer BackdropSurfaceConstants : register(b0)
{
    float4 BackdropSize;  // x textureWidth, y textureHeight
    float4 RegionRect;    // x minX, y minY, z sizeX, w sizeY
    float4 CornerRadii;   // x topLeft, y topRight, z bottomRight, w bottomLeft
}

cbuffer GlassConstants : register(b1)
{
    float4 TintColor;
    float4 EdgeColor;
    float4 GlassParams0;  // x blurMix, y distortionPx, z highlightStrength, w frostStrength
    float4 GlassParams1;  // x noiseAmount, y shadowStrength, z edgeBandPx, w chromaticPx
}

float4 PSGlass(PSInput input) : SV_Target
{
    float2 pixel = input.Position.xy;
    float2 regionMin = RegionRect.xy;
    float2 regionSize = SafeSize(RegionRect.zw);
    float2 localPixel = pixel - regionMin;
    float2 localUv = saturate(localPixel / regionSize);

    float outerSdf = RoundedRectSdf(localPixel, regionSize, CornerRadii);
    float outerAlpha = ShapeAlpha(outerSdf);
    if (outerAlpha <= 1.0e-4f)
    {
        return float4(0.0f, 0.0f, 0.0f, 0.0f);
    }

    float borderDistance = max(-outerSdf, 0.0f);
    float edgeBand = max(GlassParams1.z, 1.0f);
    float edgeSpread = edgeBand * 1.65f;
    float edgeFactor = 1.0f - smoothstep(0.0f, edgeSpread, borderDistance);
    edgeFactor = pow(saturate(edgeFactor), 0.62f);
    float interiorSpan = max(min(regionSize.x, regionSize.y) * 0.28f, edgeBand * 1.35f);
    float interiorFactor = smoothstep(edgeBand * 0.55f, interiorSpan, borderDistance);

    float2 backdropSize = SafeSize(BackdropSize.xy);
    float2 borderNormal = RoundedRectNormal(localPixel, regionSize, CornerRadii);
    float cornerFactor;
    float2 edgeNormal = SmoothEdgeNormal(localPixel, regionSize, edgeSpread, cornerFactor);
    float cornerSuppression = smoothstep(0.08f, 0.82f, cornerFactor);
    float distortionWeight = saturate(edgeFactor * (0.60f + (edgeFactor * 0.62f)));
    float directionalWeight = distortionWeight * lerp(1.0f, 0.40f, cornerSuppression);
    float chromaWeight = directionalWeight * lerp(1.0f, 0.22f, cornerSuppression);
    float distortionPx = GlassParams0.y * directionalWeight;
    float chromaticPx = GlassParams1.w * chromaWeight;
    float2 distortionUv = (edgeNormal * distortionPx) / backdropSize;
    float2 chromaUv = (edgeNormal * chromaticPx) / backdropSize;
    bool useDistortion = abs(distortionPx) > 1.0e-6f;
    bool useChroma = abs(chromaticPx) > 1.0e-6f;

    float3 blurBase = SecondaryTexture.Sample(InputSampler, input.Uv).rgb;
    float tintMix = saturate(TintColor.a);
    float3 blurTinted = ApplyTint(blurBase, TintColor.rgb, tintMix);
    float3 blurRefracted = useDistortion
        ? SecondaryTexture.Sample(InputSampler, saturate(input.Uv - (distortionUv * 0.80f))).rgb
        : blurBase;
    blurRefracted = ApplyTint(blurRefracted, TintColor.rgb, tintMix);

    float2 sharpUv = useDistortion ? saturate(input.Uv - distortionUv) : input.Uv;
    float3 sharpRgb = useChroma
        ? SampleSharpChromatic(sharpUv, chromaUv)
        : SampleSharp(sharpUv);
    float3 sharpTinted = sharpRgb * TintColor.rgb;
    float bodyBlurMix = saturate(GlassParams0.x + (interiorFactor * 0.08f));
    float blurMix = saturate(bodyBlurMix - (edgeFactor * 0.30f));
    float3 color = lerp(sharpTinted, blurTinted, blurMix);
    float edgeSharpMix = saturate(0.24f + (edgeFactor * 0.12f) - (cornerSuppression * 0.08f));
    float3 refractedRgb = lerp(blurRefracted, sharpTinted, edgeSharpMix);
    refractedRgb *= 1.0f - (edgeFactor * 0.06f);
    color = lerp(color, refractedRgb, distortionWeight * lerp(1.0f, 0.72f, cornerSuppression));

    float frost = GlassParams0.w * (0.22f + (interiorFactor * 0.20f) + (edgeFactor * 0.16f));
    color += TintColor.rgb * frost * 0.10f;

    float topBias = 1.0f - smoothstep(0.0f, 0.95f, localUv.y);
    float bottomBias = smoothstep(0.30f, 1.0f, localUv.y);
    float topFacing = saturate((-borderNormal.y * 0.5f) + 0.5f);
    float rim = pow(edgeFactor, 0.56f);
    float highlight = GlassParams0.z * rim * (0.12f + (topFacing * 0.28f)) * lerp(1.0f, 0.72f, cornerSuppression);
    color += EdgeColor.rgb * (EdgeColor.a * highlight);

    float interiorSheen = topBias * interiorFactor * 0.006f;
    color = lerp(color, float3(1.0f, 1.0f, 1.0f), interiorSheen);

    float shadow = GlassParams1.y * (0.20f + (bottomBias * 0.30f) + (interiorFactor * 0.10f) + (edgeFactor * 0.06f));
    color *= 1.0f - shadow;

    float sparkle = BlurNoiseValue((pixel * 0.45f) + (localUv * 73.0f)) * GlassParams1.x;
    color += sparkle.xxx;

    return float4(saturate(color), outerAlpha);
}
