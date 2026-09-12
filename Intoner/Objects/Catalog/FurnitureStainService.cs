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
            if (stain.Id == 0 || !stain.IsAvailable)
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
        return BuildOptions(nativeStainColors, sheetStains, optionProgress);
    }

    internal static IReadOnlyList<FurnitureStainOption> BuildOptions(
        IReadOnlyList<FurnitureStainColor> nativeStainColors,
        IReadOnlyList<SheetFurnitureStainOption> sheetStains,
        LoadProgress progress)
    {
        List<FurnitureStainOption> stains = new(nativeStainColors.Count + 1)
        {
            new(0, "Default", new Vector4(0f, 0f, 0f, 1f), false),
        };

        Dictionary<(byte R, byte G, byte B), SheetFurnitureStainOption> sheetColors = new(sheetStains.Count);
        foreach (SheetFurnitureStainOption stain in sheetStains)
        {
            progress.CancellationToken.ThrowIfCancellationRequested();
            sheetColors.TryAdd((stain.Color.R, stain.Color.G, stain.Color.B), stain);
        }

        Dictionary<uint, byte> uniqueColors = [];
        foreach (FurnitureStainColor nativeStainColor in nativeStainColors.OrderBy(static stain => stain.StainId))
        {
            progress.CancellationToken.ThrowIfCancellationRequested();
            ByteColor color = nativeStainColor.Color;
            bool matched = sheetColors.TryGetValue((color.R, color.G, color.B), out SheetFurnitureStainOption match);
            byte? duplicateOf = null;
            if (matched && !uniqueColors.TryAdd(color.RGBA, nativeStainColor.StainId))
            {
                duplicateOf = uniqueColors[color.RGBA];
            }

            stains.Add(new FurnitureStainOption(
                nativeStainColor.StainId,
                matched ? match.Name : $"Object Stain {nativeStainColor.StainId}",
                ColorUtility.ToOpaqueNormalizedColor(color),
                matched && match.IsMetallic,
                matched && !duplicateOf.HasValue,
                duplicateOf));
            progress.ReportItems(stains.Count - 1, nativeStainColors.Count);
        }

        progress.Report(1d);

        return stains.ToArray();
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
            if (row.RowId == 0 || row.Color == 0 || !row.IsHousingApplicable)
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
}

