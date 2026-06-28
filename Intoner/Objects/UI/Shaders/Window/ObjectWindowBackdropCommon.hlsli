Texture2D PrimaryTexture : register(t0);
Texture2D SecondaryTexture : register(t1);
SamplerState InputSampler : register(s0);

struct VSInput
{
    float2 Position : POSITION;
    float2 Uv : TEXCOORD0;
};

struct PSInput
{
    float4 Position : SV_POSITION;
    float2 Uv : TEXCOORD0;
};

PSInput VSMain(VSInput input)
{
    PSInput output;
    output.Position = float4(input.Position, 0.0f, 1.0f);
    output.Uv = input.Uv;
    return output;
}

float2 Hash22(float2 p)
{
    float3 x = frac(float3(p.x, p.y, p.x) * float3(0.1031f, 0.1030f, 0.0973f));
    x += dot(x, x.yzx + 19.19f);
    return frac((x.xx + x.yz) * x.zy);
}

float BlurNoiseValue(float2 position)
{
    float2 coarseNoise = Hash22(position);
    float2 fineNoise = Hash22((position * 0.1f) + 17.0f);
    return ((coarseNoise.x + fineNoise.y) * 0.5f) - 0.5f;
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
    return saturate(0.75f - sdf);
}

float2 SafeSize(float2 size)
{
    return max(size, float2(1.0f, 1.0f));
}

float2 RoundedRectNormal(float2 pixel, float2 size, float4 radii)
{
    float2 centeredPixel = pixel - (size * 0.5f);
    float radius = RoundedRectRadius(centeredPixel, radii);
    float2 halfSize = size * 0.5f;
    float2 cornerDelta = abs(centeredPixel) - (halfSize - radius.xx);
    float2 signPixel = float2(centeredPixel.x < 0.0f ? -1.0f : 1.0f, centeredPixel.y < 0.0f ? -1.0f : 1.0f);

    if (cornerDelta.x > 0.0f && cornerDelta.y > 0.0f)
    {
        return normalize(cornerDelta) * signPixel;
    }

    if (cornerDelta.x > cornerDelta.y)
    {
        return float2(signPixel.x, 0.0f);
    }

    return float2(0.0f, signPixel.y);
}

float EdgeProximity(float distance, float falloff)
{
    return 1.0f - smoothstep(0.0f, falloff, distance);
}

float2 SmoothEdgeNormal(float2 pixel, float2 size, float falloff, out float cornerFactor)
{
    float leftWeight = EdgeProximity(pixel.x, falloff);
    float rightWeight = EdgeProximity(size.x - pixel.x, falloff);
    float topWeight = EdgeProximity(pixel.y, falloff);
    float bottomWeight = EdgeProximity(size.y - pixel.y, falloff);

    float2 normal = float2(rightWeight - leftWeight, bottomWeight - topWeight);
    float normalLengthSquared = dot(normal, normal);
    cornerFactor = saturate((leftWeight + rightWeight) * (topWeight + bottomWeight));
    return normalLengthSquared > 1.0e-5f ? normalize(normal) : float2(0.0f, -1.0f);
}

float3 ApplyTint(float3 color, float3 tint, float amount)
{
    return lerp(color, color * tint, amount);
}

float3 SampleSharp(float2 sharpUv)
{
    return PrimaryTexture.Sample(InputSampler, saturate(sharpUv)).rgb;
}

float3 SampleSharpChromatic(float2 sharpUv, float2 chromaUv)
{
    return float3(
        PrimaryTexture.Sample(InputSampler, saturate(sharpUv + chromaUv)).r,
        PrimaryTexture.Sample(InputSampler, sharpUv).g,
        PrimaryTexture.Sample(InputSampler, saturate(sharpUv - chromaUv)).b);
}
