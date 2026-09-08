using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics;
using Intoner.Objects.Models;
using Intoner.Objects.Utils;
using Intoner.Services.Loading;
using Microsoft.Extensions.Logging;
using Penumbra.GameData.Structs;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Intoner.Objects.Catalog;

/// <summary> provides cached furniture stain options for the object editor and layout conversion </summary>
internal interface IFurnitureStainService : ILoadOperation
{
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
    /// <returns>true when a non default stain was found</returns>
    bool TryFindNearestStain(byte red, byte green, byte blue, out byte stainId);

    /// <summary> resolves a furniture stain id to its native byte color </summary>
    /// <param name="stainId">the stain id to resolve</param>
    /// <param name="color">the resolved byte color</param>
    /// <returns>true when the stain id exists</returns>
    bool TryResolveStainColor(byte stainId, out ByteColor color);
}

internal sealed class FurnitureStainService : IFurnitureStainService, IDisposable
{
    private readonly IFramework _framework;
    private readonly IDataManager _gameData;
    private readonly BackgroundLoad<IReadOnlyList<FurnitureStainOption>> _load;

    public FurnitureStainService(
        ILogger<FurnitureStainService> logger,
        IFramework framework,
        IDataManager gameData)
    {
        _framework = framework;
        _gameData = gameData;
        _load = new BackgroundLoad<IReadOnlyList<FurnitureStainOption>>(
            logger,
            context => BuildFurnitureStains(_framework, _gameData, context),
            new BackgroundLoadMessages(
                "building furniture stains",
                "furniture stains ready",
                "furniture stain load failed",
                "failed to build furniture stains in background"));
    }

    public LoadStatus Status
        => _load.Status;

    public void EnsureLoaded()
        => _load.EnsureLoaded();

    public bool TryGetStains([NotNullWhen(true)] out IReadOnlyList<FurnitureStainOption>? stains)
        => _load.TryGet(out stains);

    public IReadOnlyList<FurnitureStainOption> GetStains()
        => _load.Get();

    public bool TryFindNearestStain(byte red, byte green, byte blue, out byte stainId)
    {
        stainId = 0;
        int bestDistance = int.MaxValue;
        foreach (FurnitureStainOption stain in GetStains())
        {
            if (stain.Id == 0)
            {
                continue;
            }

            ByteColor stainColor = ColorUtility.ToByteColor(stain.PreviewColor);
            int distance = ColorUtility.ComputeRgbDistanceSquared(red, green, blue, stainColor.R, stainColor.G, stainColor.B);
            if (distance >= bestDistance)
            {
                continue;
            }

            bestDistance = distance;
            stainId = stain.Id;
        }

        return stainId != 0;
    }

    public bool TryResolveStainColor(byte stainId, out ByteColor color)
    {
        color = default;
        if (stainId == 0)
        {
            return false;
        }

        foreach (FurnitureStainOption stain in GetStains())
        {
            if (stain.Id != stainId)
            {
                continue;
            }

            color = ColorUtility.ToByteColor(stain.PreviewColor);
            return true;
        }

        return false;
    }

    public void Dispose()
        => _load.Dispose();

    private static IReadOnlyList<FurnitureStainOption> BuildFurnitureStains(
        IFramework framework,
        IDataManager gameData,
        LoadProgress progress)
    {
        CancellationToken cancellationToken = progress.CancellationToken;
        LoadProgressPlan progressPlan = progress.CreatePlan();
        LoadProgress nativeColorProgress = progressPlan.Next(35d);
        LoadProgress sheetProgress = progressPlan.Next(30d);
        LoadProgress optionProgress = progressPlan.Next(35d);
        IReadOnlyList<FurnitureStainColor> nativeStainColors = FurnitureStainColorUtility.CaptureNativeColors(framework, cancellationToken);
        nativeColorProgress.Report(1d);
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<SheetFurnitureStainOption> sheetStains = BuildSheetFurnitureStains(gameData, cancellationToken);
        sheetProgress.Report(1d);
        List<FurnitureStainOption> stains = new(nativeStainColors.Count + 1)
        {
            new(0, "Default", new Vector4(0f, 0f, 0f, 1f), false),
        };

        for (var index = 0; index < nativeStainColors.Count; ++index)
        {
            FurnitureStainColor nativeStainColor = nativeStainColors[index];
            cancellationToken.ThrowIfCancellationRequested();
            SheetFurnitureStainOption? match = FindSheetFurnitureStain(nativeStainColor.Color, sheetStains);
            stains.Add(new FurnitureStainOption(
                nativeStainColor.StainId,
                match?.Name ?? $"Object Stain {nativeStainColor.StainId}",
                ColorUtility.ToOpaqueNormalizedColor(nativeStainColor.Color),
                match?.IsMetallic ?? false));
            optionProgress.ReportItems(index + 1, nativeStainColors.Count);
        }

        optionProgress.Report(1d);

        return stains
            .OrderBy(static stain => stain.Id)
            .ToArray();
    }

    private static IReadOnlyList<SheetFurnitureStainOption> BuildSheetFurnitureStains(
        IDataManager gameData,
        CancellationToken cancellationToken)
    {
        List<SheetFurnitureStainOption> stains = [];
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

            Stain stain = new(row);
            string name = stain.Name.ToString();
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            stains.Add(new SheetFurnitureStainOption(
                name,
                ColorUtility.ToOpaqueByteColor(stain.R, stain.G, stain.B),
                stain.Gloss));
        }

        return stains;
    }

    private static SheetFurnitureStainOption? FindSheetFurnitureStain(
        ByteColor nativeColor,
        IReadOnlyList<SheetFurnitureStainOption> sheetStains)
    {
        for (int i = 0; i < sheetStains.Count; i++)
        {
            SheetFurnitureStainOption stain = sheetStains[i];
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

