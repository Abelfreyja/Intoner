namespace Intoner.Scene;

/// <summary> active runtime representation of one placed scene item </summary>
internal interface ISceneItemRuntime
{
    /// <summary> gets the current sanitized item state </summary>
    SceneItemSnapshot Snapshot { get; }
}

/// <summary> contributes geometry to shared scene selection </summary>
internal interface ISceneSelectableRuntime : ISceneItemRuntime
{
    /// <summary> appends draws used by the shared GPU selection pass </summary>
    void AppendSelectionDraws(SceneSelectionCollector collector);
}
