float SampleLineBeamMask(float2 pixel, float2 beamCenter, float beamW, float beamH, float scale)
{
    float horizontalScale = max(LineParams.z, 0.25f);
    float2 radius = float2(78.0f * beamW * scale * horizontalScale, 60.0f * beamH * scale);
    float d = EllipseDistance01(pixel, beamCenter, radius);
    return SampleThreeStopAlpha(d, 1.0f, 0.45f, 0.5f, 1.0f, 0.0f);
}

float SampleLineBloomMask(float2 pixel, float2 beamCenter, float beamW, float beamH, float scale)
{
    float horizontalScale = max(LineParams.z, 0.25f);
    float2 radius = float2(84.0f * beamW * scale * horizontalScale, 110.0f * beamH * scale);
    float d = EllipseDistance01(pixel, beamCenter, radius);
    return SampleThreeStopAlpha(d, 1.0f, 0.35f, 0.5f, 1.0f, 0.0f);
}

float4 SampleLineWhiteHighlight(float2 pixel, float2 beamCenter, float beamW, float beamH, float scale, bool themeDark)
{
    float horizontalScale = max(LineParams.z, 0.25f);
    float2 radius = themeDark
        ? float2(24.0f * beamW * scale * horizontalScale, 28.0f * beamH * scale)
        : float2(35.0f * beamW * scale * horizontalScale, 28.0f * beamH * scale);
    float2 center = beamCenter + float2(0.0f, 2.0f * scale);
    float d = EllipseDistance01(pixel, center, radius);

    if (themeDark)
    {
        return SampleThreeStopGradient(
            d,
            float4(1.0f, 1.0f, 1.0f, 0.38f),
            0.30f,
            float4(1.0f, 1.0f, 1.0f, 0.12f),
            0.65f,
            float4(1.0f, 1.0f, 1.0f, 0.0f));
    }

    return SampleThreeStopGradient(
        d,
        float4(0.0f, 0.0f, 0.0f, 0.60f),
        0.35f,
        float4(0.0f, 0.0f, 0.0f, 0.25f),
        0.70f,
        float4(0.0f, 0.0f, 0.0f, 0.0f));
}

float4 SampleLineBloomFieldRaw(float2 pixel, float2 beamCenter, float beamW, float beamH, float beamSpike, float beamSpike2, float scale, bool themeDark)
{
    float horizontalScale = max(LineParams.z, 0.25f);
    float thinW1 = 0.8f * horizontalScale;
    float thinW2 = 2.0f * horizontalScale;
    float thinW3 = 1.2f * horizontalScale;
    float thinW4 = 0.6f * horizontalScale;
    float thinLW = 1.0f * horizontalScale;
    float thinH1 = 92.0f;
    float thinH2 = 72.0f;
    float thinH3 = 85.0f;
    float thinH4 = 60.0f;

    float2 spikeCenters[7] =
    {
        float2(TextureSize.x * 0.08f, TextureSize.y - (2.0f * scale)),
        float2(TextureSize.x * 0.22f, TextureSize.y - (4.0f * scale)),
        float2(TextureSize.x * 0.36f, TextureSize.y - (3.0f * scale)),
        float2(TextureSize.x * 0.50f, TextureSize.y - (2.0f * scale)),
        float2(TextureSize.x * 0.64f, TextureSize.y - (4.0f * scale)),
        float2(TextureSize.x * 0.78f, TextureSize.y - (2.0f * scale)),
        float2(TextureSize.x * 0.92f, TextureSize.y - (3.0f * scale))
    };

    float2 spikeRadii[7] =
    {
        float2(thinW1 * beamSpike * scale, thinH1 * beamH * scale),
        float2(10.0f * beamSpike2 * scale * horizontalScale, 35.0f * beamH * scale),
        float2(thinW2 * (2.0f - beamSpike) * scale, thinH2 * beamH * scale),
        float2(14.0f * beamSpike2 * scale * horizontalScale, 28.0f * beamH * scale),
        float2(thinW3 * (2.0f - beamSpike2) * scale, thinH3 * beamH * scale),
        float2(7.0f * beamSpike * scale * horizontalScale, 45.0f * beamH * scale),
        float2((themeDark ? thinW4 : thinLW) * (2.0f - beamSpike) * scale, thinH4 * beamH * scale)
    };

    float4 field = float4(0.0f, 0.0f, 0.0f, 0.0f);

    if (themeDark)
    {
        float4 glowDotC = float4(1.0f, 1.0f, 1.0f, 1.0f);
        float4 glowDot20 = float4(1.0f, 1.0f, 1.0f, 0.90f);
        float4 glowDot50 = float4(1.0f, 1.0f, 1.0f, 0.50f);
        float4 glowAmbC = float4(1.0f, 1.0f, 1.0f, 0.30f);
        float4 glowAmb25 = float4(1.0f, 1.0f, 1.0f, 0.12f);
        float4 glowAmb55 = float4(1.0f, 1.0f, 1.0f, 0.03f);

        float glowDotD = EllipseDistance01(pixel, beamCenter + float2(0.0f, 1.0f * scale), float2(21.0f * beamSpike * scale * horizontalScale, 15.0f * beamSpike2 * scale));
        float4 glowDot = SampleFourStopGradient(
            glowDotD,
            glowDotC,
            0.20f,
            glowDot20,
            0.50f,
            glowDot50,
            1.0f,
            float4(1.0f, 1.0f, 1.0f, 0.0f));

        float ambientD = EllipseDistance01(pixel, beamCenter, float2(42.0f * beamW * scale * horizontalScale, 40.0f * beamH * scale));
        float4 ambientLayer = SampleFourStopGradient(
            ambientD,
            glowAmbC,
            0.25f,
            glowAmb25,
            0.55f,
            glowAmb55,
            0.80f,
            float4(1.0f, 1.0f, 1.0f, 0.0f));

        field = CompositeOver(field, ambientLayer);
        field = CompositeOver(field, glowDot);
    }
    else
    {
        float ambientD = EllipseDistance01(pixel, beamCenter, float2(50.0f * beamW * scale * horizontalScale, 32.0f * beamH * scale));
        float4 ambientLayer = SampleFourStopGradient(
            ambientD,
            float4(0.0f, 0.0f, 0.0f, 0.50f),
            0.30f,
            float4(0.0f, 0.0f, 0.0f, 0.18f),
            0.60f,
            float4(0.0f, 0.0f, 0.0f, 0.03f),
            0.85f,
            float4(0.0f, 0.0f, 0.0f, 0.0f));

        field = CompositeOver(field, ambientLayer);
    }

    float spikeStops[7] = { 0.30f, 0.50f, 0.40f, 0.55f, 0.35f, 0.48f, 0.42f };
    float spikeEnds[7] = { 0.88f, 0.95f, 0.90f, 0.96f, 0.89f, 0.94f, 0.91f };

    [unroll]
    for (int reverseIndex = 6; reverseIndex >= 0; reverseIndex--)
    {
        float d = EllipseDistance01(pixel, spikeCenters[reverseIndex], spikeRadii[reverseIndex]);
        float4 c0;
        float4 c1;

        if (reverseIndex == 0)
        {
            c0 = BloomColors[0];
            c1 = BloomColors[1];
        }
        else if (reverseIndex == 1)
        {
            c0 = BloomColors[2];
            c1 = BloomColors[3];
        }
        else
        {
            int colorIndex = reverseIndex - 2;
            c0 = BloomColors[4 + colorIndex];
            c1 = BloomColors[9 + colorIndex];
        }

        float4 layer = SampleThreeStopGradient(
            d,
            c0,
            spikeStops[reverseIndex],
            c1,
            spikeEnds[reverseIndex],
            float4(c1.rgb, 0.0f));
        field = CompositeOver(field, layer);
    }

    return field;
}

float4 ComposeLineBloomLayer(float2 pixel, float outerAlpha, bool themeDark, float strength)
{
    if (outerAlpha <= 0.0f || strength <= 0.0f || BeamMotion0.w <= 0.0f || ColorParams1.z <= 0.0f)
    {
        return float4(0.0f, 0.0f, 0.0f, 0.0f);
    }

    float scale = LayoutParams0.y;
    float beamW = BeamMotion0.y;
    float beamH = BeamMotion0.z;
    float beamEdge = BeamMotion1.x;
    if (beamEdge <= 0.0f)
    {
        return float4(0.0f, 0.0f, 0.0f, 0.0f);
    }

    float beamSpike = BeamMotion1.y;
    float beamSpike2 = BeamMotion1.z;
    float2 beamCenter = float2(BeamMotion0.x * TextureSize.x, TextureSize.y);
    float bloomMask = SampleLineBloomMask(pixel, beamCenter, beamW, beamH, scale);
    if (bloomMask <= 0.0f)
    {
        return float4(0.0f, 0.0f, 0.0f, 0.0f);
    }

    float4 bloomSource = SampleLineBloomFieldRaw(pixel, beamCenter, beamW, beamH, beamSpike, beamSpike2, scale, themeDark);
    bloomSource.a *= bloomMask;
    bloomSource.rgb = ApplyColorTransformIfNeeded(bloomSource.rgb, ColorParams0.y, ColorParams0.w, ColorParams0.z, StateParams.z > 0.5f);
    float bloomAlpha = saturate(bloomSource.a * BeamMotion0.w * beamEdge * ColorParams1.z * strength) * outerAlpha;
    return float4(bloomSource.rgb, bloomAlpha);
}
