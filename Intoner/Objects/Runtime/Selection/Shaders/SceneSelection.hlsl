cbuffer SelectionConstants : register(b0)
{
    row_major float4x4 WorldViewProjection;
    uint ItemId;
    float3 Padding;
};

struct VSInput
{
    float3 Position : POSITION;
};

struct VSOutput
{
    float4 Position : SV_Position;
    nointerpolation uint ItemId : TEXCOORD0;
};

VSOutput VSMain(VSInput input)
{
    VSOutput output;
    output.Position = mul(float4(input.Position, 1.0f), WorldViewProjection);
    output.ItemId = ItemId;
    return output;
}

uint PSMain(VSOutput input) : SV_Target0
{
    return input.ItemId;
}
