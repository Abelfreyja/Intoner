using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics;
using Intoner.Objects;
using Intoner.Objects.Models;
using Intoner.Objects.Utils;
using Microsoft.Extensions.Logging;
using Penumbra.GameData.Structs;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Intoner.Objects.UI.Services;

/// <summary> provides cached furniture stain options for the object editor </summary>
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
}

internal sealed class FurnitureStainService : IFurnitureStainService, IDisposable
{
    private readonly IFramework _framework;
    private readonly IDataManager _gameData;
    private readonly ObjectWarmupState<IReadOnlyList<FurnitureStainOption>> _warmupState;

    public FurnitureStainService(
        ILogger<FurnitureStainService> logger,
        IFramework framework,
        IDataManager gameData)
    {
        _framework = framework;
        _gameData = gameData;
        _warmupState = new ObjectWarmupState<IReadOnlyList<FurnitureStainOption>>(
            logger,
            cancellationToken => BuildFurnitureStains(_framework, _gameData, cancellationToken),
            "waiting to load furniture stains",
            "building furniture stains",
            "furniture stains ready",
            "furniture stain load failed",
            "failed to build furniture stains in background");
    }

    public bool IsReady
        => _warmupState.IsReady;

    public bool IsLoading
        => _warmupState.IsLoading;

    public bool HasFailed
        => _warmupState.HasFailed;

    public string StatusText
        => _warmupState.StatusText;

    public void EnsureWarmup()
        => _warmupState.EnsureWarmup();

    public bool TryGetStains([NotNullWhen(true)] out IReadOnlyList<FurnitureStainOption>? stains)
        => _warmupState.TryGetValue(out stains);

    public IReadOnlyList<FurnitureStainOption> GetStains()
        => _warmupState.GetValue();

    public void Dispose()
        => _warmupState.Dispose();

    private static IReadOnlyList<FurnitureStainOption> BuildFurnitureStains(
        IFramework framework,
        IDataManager gameData,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<FurnitureStainColor> nativeStainColors = FurnitureStainColorUtility.CaptureNativeColors(framework, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var sheetStains = BuildSheetFurnitureStains(gameData, cancellationToken);
        var stains = new List<FurnitureStainOption>(nativeStainColors.Count + 1)
        {
            new(0, "Default", new Vector4(0f, 0f, 0f, 1f), false),
        };

        foreach (FurnitureStainColor nativeStainColor in nativeStainColors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var match = FindSheetFurnitureStain(nativeStainColor.Color, sheetStains);
            stains.Add(new FurnitureStainOption(
                nativeStainColor.StainId,
                match?.Name ?? $"Object Stain {nativeStainColor.StainId}",
                ObjectColorUtility.ToOpaqueNormalizedColor(nativeStainColor.Color),
                match?.IsMetallic ?? false));
        }

        return stains
            .OrderBy(static stain => stain.Id)
            .ToArray();
    }

    private static IReadOnlyList<SheetFurnitureStainOption> BuildSheetFurnitureStains(
        IDataManager gameData,
        CancellationToken cancellationToken)
    {
        var stains = new List<SheetFurnitureStainOption>();
        var sheet = gameData.GetExcelSheet<Lumina.Excel.Sheets.Stain>(gameData.Language);
        if (sheet == null)
        {
            return stains;
        }

        foreach (var row in sheet)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (row.RowId == 0 || !row.IsHousingApplicable)
            {
                continue;
            }

            var stain = new Stain(row);
            var name = stain.Name.ToString();
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            stains.Add(new SheetFurnitureStainOption(
                row.RowId,
                name,
                ObjectColorUtility.ToOpaqueByteColor(stain.R, stain.G, stain.B),
                stain.Gloss));
        }

        return stains;
    }

    private static SheetFurnitureStainOption? FindSheetFurnitureStain(
        ByteColor nativeColor,
        IReadOnlyList<SheetFurnitureStainOption> sheetStains)
    {
        for (var i = 0; i < sheetStains.Count; i++)
        {
            var stain = sheetStains[i];
            if (stain.Color.R == nativeColor.R
             && stain.Color.G == nativeColor.G
             && stain.Color.B == nativeColor.B)
            {
                return stain;
            }
        }

        return null;
    }
}

