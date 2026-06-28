static const float FullStrokeSweepCenter = 0.635f;
static const float FullStrokeSolidRadius = 0.12f;
static const float FullStrokeFadeRadius = 0.28f;
static const float FullHighlightSweepCenter = 0.665f;
static const float FullHighlightSolidRadius = 0.018f;
static const float FullHighlightFadeRadius = 0.085f;
static const float FullBloomSweepCenter = 0.70f;
static const float FullBloomSolidRadius = 0.032f;
static const float FullBloomFadeRadius = 0.145f;

float FullSweepDelta(float angle01, float center)
{
    float sweepScale = max(FullBorderParams.w, 0.5f);
    float t = Wrap01(angle01 - BeamMotion0.x);
    float wrappedDelta = frac((t - center) + 0.5f) - 0.5f;
    return abs(wrappedDelta) * sweepScale;
}

float SoftSweepMask(float delta, float solidRadius, float fadeRadius)
{
    if (fadeRadius <= solidRadius)
    {
        return delta <= solidRadius ? 1.0f : 0.0f;
    }

    return 1.0f - smoothstep(solidRadius, fadeRadius, delta);
}

float SampleFullStrokeMask(float angle01)
{
    float delta = FullSweepDelta(angle01, FullStrokeSweepCenter);
    return SoftSweepMask(delta, FullStrokeSolidRadius, FullStrokeFadeRadius);
}

float4 SampleFullWhiteHighlight(float angle01, bool themeDark)
{
    float delta = FullSweepDelta(angle01, FullHighlightSweepCenter);
    float alpha = SoftSweepMask(delta, FullHighlightSolidRadius, FullHighlightFadeRadius) * (themeDark ? 0.74f : 0.58f);

    float3 rgb = themeDark ? float3(1.0f, 1.0f, 1.0f) : float3(0.0f, 0.0f, 0.0f);
    return float4(rgb, alpha);
}

float4 SampleFullBloomGradient(float angle01, bool themeDark)
{
    float delta = FullSweepDelta(angle01, FullBloomSweepCenter);
    float alpha = SoftSweepMask(delta, FullBloomSolidRadius, FullBloomFadeRadius) * (themeDark ? 0.88f : 0.72f);

    float3 rgb = themeDark ? float3(1.0f, 1.0f, 1.0f) : float3(0.0f, 0.0f, 0.0f);
    return float4(rgb, alpha);
}

float4 ComposeFullBloomLayer(float2 pixel, float ringMask, bool themeDark, float strength)
{
    if (ringMask <= 0.0f || strength <= 0.0f || BeamMotion0.w <= 0.0f || ColorParams1.z <= 0.0f)
    {
        return float4(0.0f, 0.0f, 0.0f, 0.0f);
    }

    float4 bloomSource = SampleFullBloomGradient(NormalizedConicAngle01(pixel), themeDark);
    if (bloomSource.a <= 0.0f)
    {
        return float4(0.0f, 0.0f, 0.0f, 0.0f);
    }

    float bloomAlpha = saturate(bloomSource.a * BeamMotion0.w * ColorParams1.z * strength * ringMask);
    return float4(bloomSource.rgb, bloomAlpha);
}
