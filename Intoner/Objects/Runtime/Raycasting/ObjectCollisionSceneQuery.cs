using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;

namespace Intoner.Objects.Runtime;

internal static class ObjectCollisionSceneQuery
{
    public static unsafe bool HasScene()
        => TryResolveScene(out _, out _);

    public static unsafe bool TryResolveModule(out BGCollisionModule* collisionModule)
        => TryResolveScene(out collisionModule, out _);

    private static unsafe bool TryResolveScene(out BGCollisionModule* collisionModule, out SceneWrapper* sceneWrapper)
    {
        collisionModule = null;
        sceneWrapper = null;
        Framework* framework = Framework.Instance();
        if (framework == null || framework->BGCollisionModule == null)
        {
            return false;
        }

        BGCollisionModule* resolvedCollisionModule = framework->BGCollisionModule;
        SceneManager* sceneManager = resolvedCollisionModule->SceneManager;
        if (sceneManager == null || sceneManager->FirstScene == null || sceneManager->FirstScene->Scene == null)
        {
            return false;
        }

        collisionModule = resolvedCollisionModule;
        sceneWrapper = sceneManager->FirstScene;
        return true;
    }
}
