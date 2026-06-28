cbuffer ObjectPrimitiveConstants : register(b0)
{
    float4 Viewport;
    float4 DepthParams;
    float4 DepthTextureSize;
    float4 LineParams;
    row_major float4x4 InverseProjection;
};

Texture2D<float> SceneDepth : register(t0);

static const float DepthEpsilon = 0.000001f;
static const float LineLengthEpsilon = 0.0001f;
static const float ScreenLineCapStart = 1.0f;
static const float ScreenLineCapEnd = 2.0f;
static const float ScreenLineCapBoth = 3.0f;
static const float DisabledCapDistance = -1000000.0f;

struct VSLineInput
{
    float2 AlongSide : POSITION;
    float2 LineStart : TEXCOORD0;
    float2 LineEnd : TEXCOORD1;
    float ViewDepthStart : TEXCOORD2;
    float ViewDepthEnd : TEXCOORD3;
    float InvClipWStart : TEXCOORD4;
    float InvClipWEnd : TEXCOORD5;
    float Thickness : TEXCOORD6;
    float4 Color : COLOR0;
};

struct VSPointInput
{
    float2 Position : POSITION;
    float ViewDepth : TEXCOORD0;
    float InvClipW : TEXCOORD1;
    float2 LineStart : TEXCOORD2;
    float2 LineEnd : TEXCOORD3;
    float Thickness : TEXCOORD4;
    float LineCaps : TEXCOORD5;
    float4 Color : COLOR0;
};

struct VSOutput
{
    float4 Position : SV_Position;
    float2 LineStart : TEXCOORD0;
    float2 LineEnd : TEXCOORD1;
    float ViewDepthStart : TEXCOORD2;
    float ViewDepthEnd : TEXCOORD3;
    float InvClipWStart : TEXCOORD4;
    float InvClipWEnd : TEXCOORD5;
    float Thickness : TEXCOORD6;
    nointerpolation float LineCaps : TEXCOORD7;
    float4 Color : COLOR0;
};

VSOutput CreateOutput(
    float2 position,
    float2 lineStart,
    float2 lineEnd,
    float viewDepthStart,
    float viewDepthEnd,
    float invClipWStart,
    float invClipWEnd,
    float thickness,
    float lineCaps,
    float4 color)
{
    VSOutput output;
    float2 uv = (position - Viewport.xy) / Viewport.zw;
    output.Position = float4((uv.x * 2.0f) - 1.0f, 1.0f - (uv.y * 2.0f), 0.0f, 1.0f);
    output.LineStart = lineStart;
    output.LineEnd = lineEnd;
    output.ViewDepthStart = viewDepthStart;
    output.ViewDepthEnd = viewDepthEnd;
    output.InvClipWStart = invClipWStart;
    output.InvClipWEnd = invClipWEnd;
    output.Thickness = thickness;
    output.LineCaps = lineCaps;
    output.Color = color;
    return output;
}

VSOutput VSLineMain(VSLineInput input)
{
    float2 lineVector = input.LineEnd - input.LineStart;
    float lengthSquared = dot(lineVector, lineVector);
    float invLineLength = lengthSquared > LineLengthEpsilon
        ? rsqrt(lengthSquared)
        : 0.0f;
    float2 lineDirection = lineVector * invLineLength;
    float2 lineNormal = float2(-lineDirection.y, lineDirection.x);
    float antiAliasPadding = max(LineParams.x, 0.0f);
    float capOffset = input.AlongSide.x < 0.5f
        ? -antiAliasPadding
        : antiAliasPadding;
    float2 position = lerp(input.LineStart, input.LineEnd, input.AlongSide.x)
        + (lineDirection * capOffset)
        + (lineNormal * ((input.Thickness * 0.5f) + antiAliasPadding) * input.AlongSide.y);
    return CreateOutput(
        position,
        input.LineStart,
        input.LineEnd,
        input.ViewDepthStart,
        input.ViewDepthEnd,
        input.InvClipWStart,
        input.InvClipWEnd,
        input.Thickness,
        3.0f,
        input.Color);
}

VSOutput VSPointMain(VSPointInput input)
{
    return CreateOutput(
        input.Position,
        input.LineStart,
        input.LineEnd,
        input.ViewDepth,
        input.ViewDepth,
        input.InvClipW,
        input.InvClipW,
        input.Thickness,
        input.LineCaps,
        input.Color);
}

float ResolvePrimitiveViewDepth(VSOutput input)
{
    float2 lineVector = input.LineEnd - input.LineStart;
    float lengthSquared = dot(lineVector, lineVector);
    if (lengthSquared < LineLengthEpsilon)
    {
        return input.ViewDepthStart;
    }

    float amount = saturate(dot(input.Position.xy - input.LineStart, lineVector) / lengthSquared);
    float invClipW = lerp(input.InvClipWStart, input.InvClipWEnd, amount);
    if (abs(invClipW) < DepthEpsilon)
    {
        return lerp(input.ViewDepthStart, input.ViewDepthEnd, amount);
    }

    float weightedViewDepth = lerp(
        input.ViewDepthStart * input.InvClipWStart,
        input.ViewDepthEnd * input.InvClipWEnd,
        amount);
    return weightedViewDepth / invClipW;
}

float ResolveLineCoverage(VSOutput input)
{
    float thickness = input.Thickness;
    if (thickness <= 0.0f || LineParams.y <= 0.0f)
    {
        return 1.0f;
    }

    float2 lineVector = input.LineEnd - input.LineStart;
    float lengthSquared = dot(lineVector, lineVector);
    if (lengthSquared < LineLengthEpsilon)
    {
        return 1.0f;
    }

    float lineLength = sqrt(lengthSquared);
    float2 lineDirection = lineVector / lineLength;
    float2 lineNormal = float2(-lineDirection.y, lineDirection.x);
    float2 local = input.Position.xy - input.LineStart;
    float capOverlap = max(LineParams.z, 0.0f);
    float along = dot(local, lineDirection);
    float sideDistance = abs(dot(local, lineNormal)) - (thickness * 0.5f);
    bool hasStartCap = input.LineCaps == ScreenLineCapStart || input.LineCaps == ScreenLineCapBoth;
    bool hasEndCap = input.LineCaps == ScreenLineCapEnd || input.LineCaps == ScreenLineCapBoth;
    float startDistance = hasStartCap
        ? -along - capOverlap
        : DisabledCapDistance;
    float endDistance = hasEndCap
        ? along - lineLength - capOverlap
        : DisabledCapDistance;
    float alongDistance = max(startDistance, endDistance);
    float2 distanceToRect = float2(alongDistance, sideDistance);
    float outsideDistance = length(max(distanceToRect, 0.0f));
    float insideDistance = min(max(distanceToRect.x, distanceToRect.y), 0.0f);
    float signedDistance = outsideDistance + insideDistance;
    float transitionWidth = max(max(LineParams.y, 0.0001f), fwidth(signedDistance));
    return saturate(0.5f - (signedDistance / transitionWidth));
}

float4 PSMain(VSOutput input) : SV_Target0
{
    float4 color = input.Color;
    color.a *= ResolveLineCoverage(input);
    if (color.a <= 0.0f)
    {
        discard;
    }

    if (DepthParams.x > 0.5f)
    {
        bool invertOccluded = DepthParams.x > 1.5f;
        float2 depthUv = (input.Position.xy - Viewport.xy) / Viewport.zw;
        if (any(depthUv < 0.0f) || any(depthUv > 1.0f))
        {
            discard;
        }

        float2 sceneDepthTexel = clamp(depthUv * DepthTextureSize.xy, 0.0f, DepthTextureSize.xy - 1.0f);
        float sceneDepth = SceneDepth.Load(int3((int2)sceneDepthTexel, 0));
        bool hasSceneDepth = DepthParams.z > 0.5f
            ? sceneDepth > DepthEpsilon
            : sceneDepth < 1.0f - DepthEpsilon;
        if (hasSceneDepth)
        {
            float2 ndc = float2((depthUv.x * 2.0f) - 1.0f, 1.0f - (depthUv.y * 2.0f));
            float4 sceneView = mul(float4(ndc, sceneDepth, 1.0f), InverseProjection);
            if (abs(sceneView.w) < DepthEpsilon)
            {
                discard;
            }

            float sceneViewDepth = sceneView.z / sceneView.w;
            float primitiveViewDepth = ResolvePrimitiveViewDepth(input);
            bool visible = DepthParams.w > 0.5f
                ? primitiveViewDepth <= sceneViewDepth + DepthParams.y
                : primitiveViewDepth + DepthParams.y >= sceneViewDepth;
            if (!visible)
            {
                if (invertOccluded)
                {
                    return float4(1.0f - color.rgb, color.a);
                }

                discard;
            }
        }
    }

    return color;
}
