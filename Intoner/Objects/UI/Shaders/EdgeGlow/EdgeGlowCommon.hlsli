float Wrap01(float value)
{
    return frac(value);
}

float RoundedRectRadius(float2 centeredPixel, float4 radii)
{
    if (centeredPixel.x < 0.0f)
    {
        return centeredPixel.y < 0.0f ? radii.x : radii.w;
    }

    return centeredPixel.y < 0.0f ? radii.y : radii.z;
}

float RoundedRectSdf(float2 pixel, float2 size, float4 radii)
{
    float2 centeredPixel = pixel - (size * 0.5f);
    float radius = RoundedRectRadius(centeredPixel, radii);
    float2 halfSize = size * 0.5f;
    float2 delta = abs(centeredPixel) - (halfSize - radius.xx);
    return min(max(delta.x, delta.y), 0.0f) + length(max(delta, 0.0f)) - radius;
}

float ShapeAlpha(float sdf)
{
    return saturate(0.5f - sdf);
}

float2 SafeRadius(float2 radius)
{
    return max(radius, float2(0.001f, 0.001f));
}

float EllipseDistance01(float2 pixel, float2 center, float2 radius)
{
    float2 normalized = (pixel - center) / SafeRadius(radius);
    return length(normalized);
}

float4 CompositeOver(float4 baseColor, float4 layerColor)
{
    float alpha = layerColor.a + (baseColor.a * (1.0f - layerColor.a));
    if (alpha <= 1.0e-5f)
    {
        return float4(0.0f, 0.0f, 0.0f, 0.0f);
    }

    float3 rgb = ((layerColor.rgb * layerColor.a) + (baseColor.rgb * baseColor.a * (1.0f - layerColor.a))) / alpha;
    return float4(rgb, alpha);
}

float4 SampleThreeStopGradient(float d, float4 c0, float p1, float4 c1, float p2, float4 c2)
{
    if (d >= p2)
    {
        return float4(0.0f, 0.0f, 0.0f, 0.0f);
    }

    if (d <= p1)
    {
        float t = saturate(d / max(p1, 1.0e-5f));
        return lerp(c0, c1, t);
    }

    float t = saturate((d - p1) / max(p2 - p1, 1.0e-5f));
    return lerp(c1, c2, t);
}

float4 SampleFourStopGradient(float d, float4 c0, float p1, float4 c1, float p2, float4 c2, float p3, float4 c3)
{
    if (d >= p3)
    {
        return float4(0.0f, 0.0f, 0.0f, 0.0f);
    }

    if (d <= p1)
    {
        float t = saturate(d / max(p1, 1.0e-5f));
        return lerp(c0, c1, t);
    }

    if (d <= p2)
    {
        float t = saturate((d - p1) / max(p2 - p1, 1.0e-5f));
        return lerp(c1, c2, t);
    }

    float t = saturate((d - p2) / max(p3 - p2, 1.0e-5f));
    return lerp(c2, c3, t);
}

float SampleThreeStopAlpha(float d, float a0, float p1, float a1, float p2, float a2)
{
    if (d >= p2)
    {
        return a2;
    }

    if (d <= p1)
    {
        float t = saturate(d / max(p1, 1.0e-5f));
        return lerp(a0, a1, t);
    }

    float t = saturate((d - p1) / max(p2 - p1, 1.0e-5f));
    return lerp(a1, a2, t);
}

float4 SampleSoftEllipse(float2 pixel, float2 center, float2 radius, float4 color)
{
    float d = EllipseDistance01(pixel, center, radius);
    float alpha = saturate(1.0f - d) * color.a;
    return float4(color.rgb, alpha);
}

float3 HsvToRgb(float3 hsv)
{
    const float4 K = float4(1.0f, 2.0f / 3.0f, 1.0f / 3.0f, 3.0f);
    float3 p = abs(frac(hsv.xxx + K.xyz) * 6.0f - K.www);
    return hsv.z * lerp(K.xxx, saturate(p - K.xxx), hsv.y);
}

float3 HueRotate(float3 color, float hueShiftDeg, float saturationMul, float brightnessMul)
{
    const float4 rgbToHsv = float4(0.0f, -1.0f / 3.0f, 2.0f / 3.0f, -1.0f);
    float4 p = lerp(float4(color.bg, rgbToHsv.wz), float4(color.gb, rgbToHsv.xy), step(color.b, color.g));
    float4 q = lerp(float4(p.xyw, color.r), float4(color.r, p.yzx), step(p.x, color.r));
    float d = q.x - min(q.w, q.y);
    float e = 1.0e-10f;
    float3 hsv = float3(abs(q.z + (q.w - q.y) / (6.0f * d + e)), d / (q.x + e), q.x);

    hsv.x = frac(hsv.x + (hueShiftDeg / 360.0f));
    hsv.y = saturate(hsv.y * saturationMul);
    hsv.z = saturate(hsv.z * brightnessMul);
    return HsvToRgb(hsv);
}

float3 ApplyColorTransformIfNeeded(float3 color, float hueShiftDeg, float saturationMul, float brightnessMul, bool shouldApply)
{
    if (!shouldApply)
    {
        return color;
    }

    return HueRotate(color, hueShiftDeg, saturationMul, brightnessMul);
}

float NormalizedConicAngle01(float2 pixel)
{
    float2 centered = pixel - (TextureSize * 0.5f);
    return Wrap01((atan2(centered.y, centered.x) + (0.5f * Pi)) / Tau);
}

float4 ComposeStrokeLayer(float4 strokeSource, float maskAlpha, float ringMask, float strength)
{
    float strokeAlpha = saturate(strokeSource.a * BeamMotion0.w * maskAlpha * ringMask * ColorParams1.x * strength);
    return float4(strokeSource.rgb, strokeAlpha);
}

float4 ComposeInnerLayer(float4 innerField, float outerSdf, float outerAlpha, float maskAlpha, float innerShadowBlur, bool themeDark, float strength)
{
    float4 innerSource = innerField;
    if (ColorParams1.w > 0.0f)
    {
        float distanceFromOuter = max(-outerSdf, 0.0f);
        float shadowDistance = max(distanceFromOuter - LayoutParams0.x, 0.0f);
        float shadowBlur = max(innerShadowBlur, 1.0f);
        float innerShadowStrength = exp(-((shadowDistance * shadowDistance) / max(1.0f, 2.0f * shadowBlur * shadowBlur))) * ColorParams1.w;
        float3 shadowColor = themeDark ? float3(1.0f, 1.0f, 1.0f) : float3(0.0f, 0.0f, 0.0f);
        innerSource = CompositeOver(innerSource, float4(shadowColor, innerShadowStrength));
    }

    innerSource.rgb = ApplyColorTransformIfNeeded(innerSource.rgb, ColorParams0.x, ColorParams0.w, ColorParams0.z, StateParams.y > 0.5f);
    float innerAlpha = saturate(innerSource.a * BeamMotion0.w * maskAlpha * outerAlpha * ColorParams1.y * strength);
    return float4(innerSource.rgb, innerAlpha);
}

BorderMaskData ComputeBorderMask(float2 pixel, bool isFullBorder)
{
    BorderMaskData mask;
    mask.OuterSdf = RoundedRectSdf(pixel, TextureSize, OuterRadii);
    mask.OuterAlpha = ShapeAlpha(mask.OuterSdf);

    float2 innerInset = LayoutParams0.x.xx;
    float2 innerPixel = pixel - innerInset;
    float2 innerSize = max(TextureSize - (innerInset * 2.0f), float2(1.0f, 1.0f));
    float innerSdf = RoundedRectSdf(innerPixel, innerSize, InnerRadii);
    float innerAlpha = ShapeAlpha(innerSdf);
    float borderDistance = max(-mask.OuterSdf, 0.0f);
    float innerReachScale = isFullBorder ? max(FullBorderParams.z, 0.5f) : 1.0f;
    float innerBandPx = isFullBorder ? FullBorderParams.x : LineParams.x;
    float innerBand = min(innerBandPx * innerReachScale, min(TextureSize.x, TextureSize.y) * (0.24f * innerReachScale));
    float innerBandFeather = isFullBorder
        ? max(innerBand * 0.38f, 2.0f * LayoutParams0.y)
        : max(1.0f * LayoutParams0.y, 0.75f);
    mask.RingMask = saturate(mask.OuterAlpha - innerAlpha);
    mask.InnerEdgeMask = 1.0f - smoothstep(
        innerBand - innerBandFeather,
        innerBand + innerBandFeather,
        borderDistance);
    return mask;
}

float4 SampleStrokeSpotField(float2 pixel, float2 centerOrigin, float2 radiusScale)
{
    float4 field = float4(0.0f, 0.0f, 0.0f, 0.0f);

    [unroll]
    for (int index = SpotCount - 1; index >= 0; index--)
    {
        float4 spot = StrokeSpots[index];
        float4 color = StrokeColors[index];
        float2 center = centerOrigin + spot.xy;
        float2 radius = spot.zw * radiusScale;
        float4 layer = SampleSoftEllipse(pixel, center, radius, color);
        field = CompositeOver(field, layer);
    }

    return field;
}

float4 SampleInnerSpotField(float2 pixel, float2 centerOrigin, float2 radiusScale)
{
    float4 field = float4(0.0f, 0.0f, 0.0f, 0.0f);

    [unroll]
    for (int index = SpotCount - 1; index >= 0; index--)
    {
        float4 spot = InnerSpots[index];
        float4 color = InnerColors[index];
        float2 center = centerOrigin + spot.xy;
        float2 radius = spot.zw * radiusScale;
        float4 layer = SampleSoftEllipse(pixel, center, radius, color);
        field = CompositeOver(field, layer);
    }

    return field;
}

float4 SampleBeamTexturePremultiplied(float2 uv)
{
    float4 sample = EdgeGlowTexture.Sample(EdgeGlowSampler, uv);
    return float4(sample.rgb * sample.a, sample.a);
}

float4 ResolvePremultiplied(float4 sample)
{
    if (sample.a <= 1.0e-5f)
    {
        return float4(0.0f, 0.0f, 0.0f, 0.0f);
    }

    return float4(sample.rgb / sample.a, sample.a);
}
