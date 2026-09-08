cbuffer WorldSurfaceConstants : register(b0)
{
    row_major float4x4 WorldViewProjection;
    row_major float4x4 WorldView;
    row_major float4x4 InverseProjection;
    float4 Viewport;
    float4 DepthParams;
    float4 DepthTextureSize;
    float4 SurfaceParams;
    float4 Tint;
    float4 UvRect;
    float4 FrameParams;
};

Texture2D<float> SceneDepth : register(t0);
Texture2D<float4> SurfaceTexture : register(t1);
SamplerState SurfaceSampler : register(s0);

#include "../../Scene/Shaders/SceneDepth.hlsli"

static const float2 QuadPositions[6] =
{
    float2(-0.5f, 0.5f),
    float2(0.5f, 0.5f),
    float2(0.5f, -0.5f),
    float2(-0.5f, 0.5f),
    float2(0.5f, -0.5f),
    float2(-0.5f, -0.5f),
};

static const float2 QuadUvs[6] =
{
    float2(0.0f, 0.0f),
    float2(1.0f, 0.0f),
    float2(1.0f, 1.0f),
    float2(0.0f, 0.0f),
    float2(1.0f, 1.0f),
    float2(0.0f, 1.0f),
};

struct VSOutput
{
    float4 Position : SV_Position;
    float2 Uv : TEXCOORD0;
    float ViewDepth : TEXCOORD1;
};

VSOutput VSMain(uint vertexId : SV_VertexID)
{
    VSOutput output;
    float4 localPosition = float4(QuadPositions[vertexId] * SurfaceParams.xy, 0.0f, 1.0f);
    output.Position = mul(localPosition, WorldViewProjection);
    output.Uv = lerp(UvRect.xy, UvRect.zw, QuadUvs[vertexId]);
    output.ViewDepth = mul(localPosition, WorldView).z;
    return output;
}

float4 PSMain(VSOutput input, bool isFrontFace : SV_IsFrontFace) : SV_Target0
{
    if (!isFrontFace && SurfaceParams.z < 0.5f)
    {
        discard;
    }

    if (DepthParams.x > 0.5f)
    {
        float2 depthUv = ResolveSceneDepthUv(input.Position.xy);
        if (!IsInsideSceneDepth(depthUv))
        {
            discard;
        }

        float sceneViewDepth;
        bool invalidSceneDepth;
        if (TryResolveSceneViewDepth(depthUv, sceneViewDepth, invalidSceneDepth))
        {
            if (!IsSceneDepthVisible(input.ViewDepth, sceneViewDepth))
            {
                discard;
            }
        }
        else if (invalidSceneDepth)
        {
            discard;
        }
    }

    float4 color = SurfaceTexture.Sample(SurfaceSampler, input.Uv);
    float sourceAlpha = saturate(color.a);
    if (FrameParams.x < 0.5f)
    {
        sourceAlpha = 1.0f;
    }
    else if (FrameParams.x > 1.5f)
    {
        color.rgb = sourceAlpha > 0.000001f
            ? color.rgb / sourceAlpha
            : 0.0f;
    }

    color.a = sourceAlpha;
    color *= Tint;
    color.a = saturate(color.a);
    if (color.a <= 0.0f)
    {
        discard;
    }

    return color;
}
