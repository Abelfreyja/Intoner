// blur base adapted from https://github.com/itsRythem/ImGui-Blur
#include "ObjectWindowBackdropCommon.hlsli"

cbuffer BackdropBlurConstants : register(b0)
{
    float4 BlurParams;  // x halfPixelX, y halfPixelY, z offset, w noise
}

float4 PSDownsample(PSInput input) : SV_Target
{
    float2 delta = BlurParams.xy * BlurParams.z;

    float4 sum = PrimaryTexture.Sample(InputSampler, input.Uv) * 4.0f;
    sum += PrimaryTexture.Sample(InputSampler, input.Uv - delta);
    sum += PrimaryTexture.Sample(InputSampler, input.Uv + delta);
    sum += PrimaryTexture.Sample(InputSampler, input.Uv + float2(delta.x, -delta.y));
    sum += PrimaryTexture.Sample(InputSampler, input.Uv - float2(delta.x, -delta.y));
    float4 result = sum / 8.0f;
    if (BlurParams.w > 0.0f)
    {
        result.rgb += BlurNoiseValue(input.Position.xy) * BlurParams.w * 0.3f;
    }

    return result;
}

float4 PSUpsample(PSInput input) : SV_Target
{
    float2 delta = BlurParams.xy * BlurParams.z;

    float4 sum = PrimaryTexture.Sample(InputSampler, input.Uv + float2(-delta.x * 2.0f, 0.0f));
    sum += PrimaryTexture.Sample(InputSampler, input.Uv + float2(-delta.x, delta.y)) * 2.0f;
    sum += PrimaryTexture.Sample(InputSampler, input.Uv + float2(0.0f, delta.y * 2.0f));
    sum += PrimaryTexture.Sample(InputSampler, input.Uv + float2(delta.x, delta.y)) * 2.0f;
    sum += PrimaryTexture.Sample(InputSampler, input.Uv + float2(delta.x * 2.0f, 0.0f));
    sum += PrimaryTexture.Sample(InputSampler, input.Uv + float2(delta.x, -delta.y)) * 2.0f;
    sum += PrimaryTexture.Sample(InputSampler, input.Uv + float2(0.0f, -delta.y * 2.0f));
    sum += PrimaryTexture.Sample(InputSampler, input.Uv + float2(-delta.x, -delta.y)) * 2.0f;
    float4 result = sum / 12.0f;
    if (BlurParams.w > 0.0f)
    {
        result.rgb += BlurNoiseValue(input.Position.xy) * BlurParams.w * 0.3f;
    }

    return result;
}

float4 PSComposite(PSInput input) : SV_Target
{
    float4 game = PrimaryTexture.Sample(InputSampler, input.Uv);
    float4 overlay = SecondaryTexture.Sample(InputSampler, input.Uv);
    float3 color = overlay.rgb + (game.rgb * (1.0f - overlay.a));
    return float4(color, 1.0f);
}
