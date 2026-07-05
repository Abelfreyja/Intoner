using FFXIVClientStructs.FFXIV.Client.Graphics;
using Intoner.Objects.Models;
using System.Diagnostics.CodeAnalysis;

namespace Intoner.Objects.Catalog;

/// <summary> provides cached furniture stain options for the object editor and layout conversion </summary>
internal interface IFurnitureStainService
{
    /// <summary> gets whether the stain list has finished loading </summary>
    bool IsReady { get; }

    /// <summary> gets whether the stain list is currently loading in the background </summary>
    bool IsLoading { get; }

    /// <summary> gets whether the most recent background load failed </summary>
    bool HasFailed { get; }

    /// <summary> gets the current warmup status text </summary>
    string StatusText { get; }

    /// <summary> starts background stain warmup if needed </summary>
    void EnsureWarmup();

    /// <summary> gets the stain list if it is already ready </summary>
    /// <param name="stains">the ready stain list when available</param>
    /// <returns>true when the stain list is already ready</returns>
    bool TryGetStains([NotNullWhen(true)] out IReadOnlyList<FurnitureStainOption>? stains);

    /// <summary> gets the stain list, blocking until it is ready if needed </summary>
    /// <returns>the ready stain list</returns>
    IReadOnlyList<FurnitureStainOption> GetStains();

    /// <summary> resolves the closest furniture stain to an imported RGB color </summary>
    /// <param name="red">the red byte component</param>
    /// <param name="green">the green byte component</param>
    /// <param name="blue">the blue byte component</param>
    /// <param name="stainId">the resolved stain id</param>
    /// <returns>true when a non-default stain was found</returns>
    bool TryFindNearestStain(byte red, byte green, byte blue, out byte stainId);

    /// <summary> resolves a furniture stain id to its native byte color </summary>
    /// <param name="stainId">the stain id to resolve</param>
    /// <param name="color">the resolved byte color</param>
    /// <returns>true when the stain id exists</returns>
    bool TryResolveStainColor(byte stainId, out ByteColor color);
}
