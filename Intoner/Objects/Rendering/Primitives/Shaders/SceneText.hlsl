cbuffer TextConstants : register(b0)
{
    float4 Viewport;
    float4 Rectangle;
};

Texture2D Label : register(t0);
SamplerState LabelSampler : register(s0);

struct VSOutput
{
    float4 Position : SV_Position;
    float2 UV : TEXCOORD0;
};

VSOutput VSMain(uint vertex : SV_VertexID)
{
    static const float2 corners[6] = {
        float2(0, 0), float2(1, 0), float2(1, 1),
        float2(0, 0), float2(1, 1), float2(0, 1)
    };
    VSOutput output;
    output.UV = corners[vertex];
    float2 pixel = Rectangle.xy + output.UV * Rectangle.zw;
    float2 ndc = (pixel - Viewport.xy) / Viewport.zw;
    output.Position = float4(ndc.x * 2 - 1, 1 - ndc.y * 2, 0, 1);
    return output;
}

float4 PSMain(VSOutput input) : SV_Target
{
    return Label.Sample(LabelSampler, input.UV);
}
