using Lumina;
using Intoner.Objects.Assets.Cache;
using Microsoft.Extensions.Logging;
using Intoner.Logging;

namespace Intoner.Objects.Assets;

internal readonly record struct SqpackIndex1EntryKey(int IndexId, uint FolderHash, uint FileHash);

internal readonly record struct SqpackIndex2EntryKey(int IndexId, uint FullHash);

internal readonly record struct SqpackPathHash(int IndexId, uint FolderHash, uint FileHash, uint FullHash)
{
    public static bool TryCompute(string path, out SqpackPathHash sqpackPathHash)
    {
        string normalizedPath = GameAssetPathRules.NormalizeGamePath(path).ToLowerInvariant();
        ParsedFilePath? parsedPath;
        try
        {
            parsedPath = GameData.ParseFilePath(normalizedPath);
        }
        catch
        {
            sqpackPathHash = default;
            return false;
        }

        if (parsedPath is null)
        {
            sqpackPathHash = default;
            return false;
        }

        if (!TryResolveIndexId(normalizedPath, parsedPath, out int indexId))
        {
            sqpackPathHash = default;
            return false;
        }

        sqpackPathHash = new SqpackPathHash(
            indexId,
            parsedPath.FolderHash,
            parsedPath.FileHash,
            parsedPath.Index2Hash);
        return true;
    }

    private static bool TryResolveIndexId(string normalizedPath, ParsedFilePath parsedPath, out int indexId)
    {
        if (!TryResolveCategoryId(parsedPath.Category, out int categoryId))
        {
            indexId = -1;
            return false;
        }

        if (!TryResolveRepositoryId(parsedPath.Repository, out int repositoryId))
        {
            indexId = -1;
            return false;
        }

        if (!TryResolveExtraId(categoryId, normalizedPath, repositoryId, out int extraId))
        {
            indexId = -1;
            return false;
        }

        int repositoryComponent = categoryId is 0x02 or 0x03 or 0x0C
            ? repositoryId << 8
            : 0;

        indexId = (categoryId << 16) | repositoryComponent | extraId;
        return true;
    }

    private static bool TryResolveExtraId(int categoryId, string normalizedPath, int repositoryId, out int extraId)
    {
        if (categoryId == 0x02)
        {
            return TryResolveBgSubId(normalizedPath, repositoryId, out extraId);
        }

        extraId = 0;
        return true;
    }

    private static bool TryResolveCategoryId(string category, out int categoryId)
    {
        categoryId = category switch
        {
            "common" => 0x00,
            "bgcommon" => 0x01,
            "bg" => 0x02,
            "cut" => 0x03,
            "chara" => 0x04,
            "shader" => 0x05,
            "ui" => 0x06,
            "sound" => 0x07,
            "vfx" => 0x08,
            "ui_script" => 0x09,
            "exd" => 0x0A,
            "game_script" => 0x0B,
            "music" => 0x0C,
            "_sq" => 0x12,
            "_de" => 0x13,
            _ => -1,
        };

        return categoryId >= 0;
    }

    private static bool TryResolveRepositoryId(string repository, out int repositoryId)
    {
        if (string.Equals(repository, "ffxiv", StringComparison.Ordinal))
        {
            repositoryId = 0;
            return true;
        }

        if (repository.Length >= 3
         && repository.StartsWith("ex", StringComparison.Ordinal)
         && int.TryParse(repository.AsSpan(2), out int expansionId)
         && expansionId is >= 0 and <= 0xff)
        {
            repositoryId = expansionId;
            return true;
        }

        repositoryId = -1;
        return false;
    }

    private static bool TryResolveBgSubId(string normalizedPath, int repositoryId, out int bgSubId)
    {
        if (repositoryId == 0)
        {
            bgSubId = 0;
            return true;
        }

        const int bgPrefixLength = 3;
        int repositoryEnd = normalizedPath.IndexOf('/', bgPrefixLength);
        if (repositoryEnd < 0)
        {
            bgSubId = -1;
            return false;
        }

        int segmentOffset = repositoryEnd + 1;
        return TryParseDigits(normalizedPath, segmentOffset, 2, out bgSubId);
    }

    private static bool TryParseDigits(string value, int offset, int length, out int result)
    {
        if (offset < 0 || offset + length > value.Length)
        {
            result = -1;
            return false;
        }

        result = 0;
        for (int i = offset; i < offset + length; i++)
        {
            if (!char.IsAsciiDigit(value[i]))
            {
                result = -1;
                return false;
            }

            result *= 10;
            result += value[i] - '0';
        }

        return true;
    }
}

internal sealed record SqpackIndexSnapshot(
    string GameVersion,
    IReadOnlySet<SqpackIndex1EntryKey> Index1Entries,
    IReadOnlySet<SqpackIndex2EntryKey> Index2Entries,
    IReadOnlyList<SqpackNamedPath> NamedPaths)
{
    private readonly HashSet<string> _normalizedNamedPathSet = NamedPaths
        .Select(static path => GameAssetPathRules.NormalizeGamePath(path.Path))
        .Where(static path => !string.IsNullOrWhiteSpace(path))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public bool HasEntries
        => Index1Entries.Count > 0 || Index2Entries.Count > 0 || NamedPaths.Count > 0;

    public bool ContainsPath(string path)
    {
        string normalizedPath = GameAssetPathRules.NormalizeGamePath(path);
        if (_normalizedNamedPathSet.Contains(normalizedPath))
        {
            return true;
        }

        if (!SqpackPathHash.TryCompute(path, out SqpackPathHash sqpackPathHash))
        {
            return false;
        }

        return Index1Entries.Contains(new SqpackIndex1EntryKey(
                   sqpackPathHash.IndexId,
                   sqpackPathHash.FolderHash,
                   sqpackPathHash.FileHash))
            || Index2Entries.Contains(new SqpackIndex2EntryKey(
                   sqpackPathHash.IndexId,
                   sqpackPathHash.FullHash));
    }
}

internal sealed class SqpackIndexStore
{
    private readonly ILogger<SqpackIndexStore> _logger;
    private readonly IObjectAssetGameVersionService _gameVersionService;
    private readonly Lock _loadLock = new();

    private SqpackIndexSnapshot? _snapshot;

    public SqpackIndexStore(
        ILogger<SqpackIndexStore> logger,
        IObjectAssetGameVersionService gameVersionService)
    {
        _logger             = logger;
        _gameVersionService = gameVersionService;
    }

    public SqpackIndexSnapshot Load(CancellationToken cancellationToken = default)
    {
        lock (_loadLock)
        {
            if (_snapshot is not null)
            {
                return _snapshot;
            }

            if (!SqpackIndexFileSystem.TryResolveSqpackRoot(out string sqpackRoot))
            {
                _snapshot = new SqpackIndexSnapshot(
                    string.Empty,
                    new HashSet<SqpackIndex1EntryKey>(),
                    new HashSet<SqpackIndex2EntryKey>(),
                    []);
                return _snapshot;
            }

            string gameVersion = _gameVersionService.GetCurrentGameVersion();
            ValueStopwatch stopwatch = ValueStopwatch.StartNew();
            HashSet<SqpackIndex1EntryKey> index1Entries = new();
            HashSet<SqpackIndex2EntryKey> index2Entries = new();
            HashSet<string> namedPaths = new(StringComparer.OrdinalIgnoreCase);

            foreach (string indexFilePath in SqpackIndexFileSystem.EnumerateIndexFiles(sqpackRoot))
            {
                cancellationToken.ThrowIfCancellationRequested();
                TryLoadIndexFile(indexFilePath, index1Entries, index2Entries, namedPaths, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            _snapshot = new SqpackIndexSnapshot(
                gameVersion,
                index1Entries,
                index2Entries,
                namedPaths
                    .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
                    .Select(static path => new SqpackNamedPath(path))
                    .ToArray());

            _logger.LogInformation(
                "loaded sqpack index store with {Index1Count} index1 entries, {Index2Count} index2 entries, and {NamedPathCount} named sqpack paths in {ElapsedMs}ms",
                _snapshot.Index1Entries.Count,
                _snapshot.Index2Entries.Count,
                _snapshot.NamedPaths.Count,
                stopwatch.ElapsedMilliseconds);

            return _snapshot;
        }
    }

    private void TryLoadIndexFile(
        string indexFilePath,
        ISet<SqpackIndex1EntryKey> index1Entries,
        ISet<SqpackIndex2EntryKey> index2Entries,
        ISet<string> namedPaths,
        CancellationToken cancellationToken)
    {
        try
        {
            SqpackIndexFile sqpackIndexFile = SqpackIndexFile.Read(indexFilePath, cancellationToken);
            foreach (SqpackIndex1EntryKey entry in sqpackIndexFile.Index1Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _ = index1Entries.Add(entry);
            }

            foreach (SqpackIndex2EntryKey entry in sqpackIndexFile.Index2Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _ = index2Entries.Add(entry);
            }

            foreach (SqpackNamedPath namedPath in sqpackIndexFile.NamedPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string normalizedPath = GameAssetPathRules.NormalizeGamePath(namedPath.Path);
                if (!string.IsNullOrWhiteSpace(normalizedPath))
                {
                    _ = namedPaths.Add(normalizedPath);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "failed to read sqpack index file {IndexFilePath}", indexFilePath);
        }
    }
}

