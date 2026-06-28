cbuffer ObjectSelectionConstants : register(b0)
{
    row_major float4x4 WorldViewProjection;
    uint ObjectId;
    float3 Padding;
};

struct VSInput
{
    float3 Position : POSITION;
};

struct VSOutput
{
    float4 Position : SV_Position;
    nointerpolation uint ObjectId : TEXCOORD0;
};

VSOutput VSMain(VSInput input)
{
    VSOutput output;
    output.Position = mul(float4(input.Position, 1.0f), WorldViewProjection);
    output.ObjectId = ObjectId;
    return output;
}

uint PSMain(VSOutput input) : SV_Target0
{
    return input.ObjectId;
}
