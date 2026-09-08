using System.Diagnostics.CodeAnalysis;

namespace Intoner.Scene;

/// <summary> resolves the current scene location and creation context </summary>
internal interface ISceneLocationService
{
    /// <summary> raised when the game starts changing location </summary>
    event Action? TransitionStarted;

    /// <summary> raised after a game location transition may have changed the current scope </summary>
    event Action? LocationInvalidated;

    /// <summary> gets whether the game is currently changing location </summary>
    bool IsTransitioning { get; }

    /// <summary> gets public worlds ordered by data center and world name </summary>
    IReadOnlyList<SceneWorldInfo> PublicWorlds { get; }

    /// <summary> resolves a world and its data center from the game sheets </summary>
    /// <param name="worldId">the world row id</param>
    /// <param name="world">the resolved world when found</param>
    /// <returns>true when the world has a name and a valid data center</returns>
    bool TryResolveWorld(ushort worldId, [NotNullWhen(true)] out SceneWorldInfo? world);

    /// <summary> gets the current creation context for new placed items </summary>
    SceneCreationContext GetCurrentCreationContext();

    /// <summary> gets the current location scope used for item activation </summary>
    SceneLocationScope GetCurrentLocationScope();
}
