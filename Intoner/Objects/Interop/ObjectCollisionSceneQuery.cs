using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using System.Runtime.InteropServices;

namespace Intoner.Objects.Interop;

internal static class ObjectCollisionSceneQuery
{
    public static unsafe bool HasScene()
        => TryResolveScene(out _, out _);

    public static unsafe bool TryResolveModule(out BGCollisionModule* collisionModule)
        => TryResolveScene(out collisionModule, out _);

    public static unsafe bool TryResolveScene(out BGCollisionModule* collisionModule, out SceneWrapper* sceneWrapper)
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

/// <summary> uses the game's collision update lock </summary>
internal readonly unsafe ref struct CollisionLock
{
    private readonly nint* _address;
    private readonly bool _exclusive;

    private CollisionLock(BGCollisionModule* module, bool exclusive)
    {
        _address = &module->UpdateTaskLock;
        _exclusive = exclusive;
        if (exclusive)
        {
            AcquireSRWLockExclusive(_address);
        }
        else
        {
            AcquireSRWLockShared(_address);
        }
    }

    public static CollisionLock Read(BGCollisionModule* module) => new(module, false);
    public static CollisionLock Write(BGCollisionModule* module) => new(module, true);

    public void Dispose()
    {
        if (_exclusive)
        {
            ReleaseSRWLockExclusive(_address);
        }
        else
        {
            ReleaseSRWLockShared(_address);
        }
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern void AcquireSRWLockShared(nint* address);
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern void ReleaseSRWLockShared(nint* address);
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern void AcquireSRWLockExclusive(nint* address);
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern void ReleaseSRWLockExclusive(nint* address);
}