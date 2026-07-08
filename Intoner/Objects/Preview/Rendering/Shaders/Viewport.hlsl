cbuffer ViewportFrameConstants : register(b0)
{
    row_major float4x4 ViewProjection;
    float4 LightDirection;
    float4 BackgroundTop;
    float4 BackgroundBottom;
};

cbuffer ViewportMaterialConstants : register(b1)
{
    float4 UntexturedDiffuseColor;
    float4 MaterialParams;
};

Texture2D<float4> DiffuseTexture : register(t0);
SamplerState DiffuseSampler : register(s0);

static const float AlphaClipThreshold = 0.10f;

struct VSMeshInput
{
    float3 Position : POSITION;
    float3 Normal : NORMAL;
    float2 TexCoord : TEXCOORD0;
};

struct VSMeshOutput
{
    float4 Position : SV_Position;
    float3 Normal : NORMAL;
    float2 TexCoord : TEXCOORD0;
};

struct VSBackgroundOutput
{
    float4 Position : SV_Position;
    float2 Uv : TEXCOORD0;
};

VSMeshOutput VSMeshMain(VSMeshInput input)
{
    VSMeshOutput output;
    output.Position = mul(float4(input.Position, 1.0f), ViewProjection);
    output.Normal = normalize(input.Normal);
    output.TexCoord = input.TexCoord;
    return output;
}

VSBackgroundOutput VSBackgroundMain(uint vertexId : SV_VertexID)
{
    float2 position = float2((vertexId << 1) & 2, vertexId & 2);

    VSBackgroundOutput output;
    output.Position = float4((position * float2(2.0f, -2.0f)) + float2(-1.0f, 1.0f), 0.0f, 1.0f);
    output.Uv = position;
    return output;
}

float4 PSBackgroundMain(VSBackgroundOutput input) : SV_Target0
{
    return float4(lerp(BackgroundTop.rgb, BackgroundBottom.rgb, saturate(input.Uv.y)), 1.0f);
}

float4 PSMeshMain(VSMeshOutput input) : SV_Target0
{
    float useTexture = MaterialParams.x;
    float applyAlphaClip = MaterialParams.y;
    float transparency = MaterialParams.z;

    float4 sampled = DiffuseTexture.Sample(DiffuseSampler, input.TexCoord);
    float3 diffuseColor = lerp(UntexturedDiffuseColor.rgb, sampled.rgb, useTexture);
    float alpha = lerp(UntexturedDiffuseColor.a, sampled.a, useTexture) * transparency;

    if (applyAlphaClip > 0.5f && alpha < AlphaClipThreshold)
    {
        discard;
    }

    float3 normal = normalize(input.Normal);
    float diffuse = saturate(abs(dot(normal, normalize(LightDirection.xyz))));
    float lighting = 0.38f + (diffuse * 0.62f);
    return float4(diffuseColor * lighting, alpha);
}
