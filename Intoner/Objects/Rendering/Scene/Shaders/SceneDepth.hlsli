static const float SceneDepthEpsilon = 0.000001f;

float2 ResolveSceneDepthUv(float2 screenPosition)
{
    return (screenPosition - Viewport.xy) / Viewport.zw;
}

bool IsInsideSceneDepth(float2 depthUv)
{
    return all(depthUv >= 0.0f) && all(depthUv <= 1.0f);
}

bool TryResolveSceneViewDepth(float2 depthUv, out float sceneViewDepth, out bool invalidSceneDepth)
{
    invalidSceneDepth = false;
    float2 sceneDepthTexel = clamp(depthUv * DepthTextureSize.xy, 0.0f, DepthTextureSize.xy - 1.0f);
    float sceneDepth = SceneDepth.Load(int3((int2)sceneDepthTexel, 0));
    bool hasSceneDepth = DepthParams.z > 0.5f
        ? sceneDepth > SceneDepthEpsilon
        : sceneDepth < 1.0f - SceneDepthEpsilon;
    if (!hasSceneDepth)
    {
        sceneViewDepth = 0.0f;
        return false;
    }

    float2 ndc = float2((depthUv.x * 2.0f) - 1.0f, 1.0f - (depthUv.y * 2.0f));
    float4 sceneView = mul(float4(ndc, sceneDepth, 1.0f), InverseProjection);
    if (abs(sceneView.w) < SceneDepthEpsilon)
    {
        sceneViewDepth = 0.0f;
        invalidSceneDepth = true;
        return false;
    }

    sceneViewDepth = sceneView.z / sceneView.w;
    return true;
}

bool IsSceneDepthVisible(float drawableViewDepth, float sceneViewDepth)
{
    return DepthParams.w > 0.5f
        ? drawableViewDepth <= sceneViewDepth + DepthParams.y
        : drawableViewDepth + DepthParams.y >= sceneViewDepth;
}
