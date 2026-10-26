using Intoner.Objects.Resources;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Buffers.Binary;
using static Penumbra.GameData.Files.TmbFile.TmbEntry;

namespace Intoner.Objects.Assets
{
    internal static class TmbVfxReferenceReader
    {
        private enum TmbVfxVisibility
        {
            DefaultNoTriggers = 0,
            DefaultWithTriggers = 1,
            AlwaysNoTriggers = 2,
            AlwaysWithTriggers = 3,
        }

        public static TmbVfxReferenceCollection CollectReferences(
            IObjectAssetGameData gameData,
            string tmbPath,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string normalizedPath = ObjectResolvedPath.FromGamePath(tmbPath).Path;

            if (!GameAssetPathRules.IsFileKind(normalizedPath, GameAssetFileKind.Tmb))
            {
                return new([], false);
            }

            try
            {
                ObjectAssetFileData file = ObjectAssetFileUtility.ReadGameFile(gameData, normalizedPath, cancellationToken);
                return file.Data is null
                    ? new([], file.IsComplete)
                    : CollectReferences(gameData, file.Data, normalizedPath, cancellationToken);
            }
            catch (Exception ex) when (ObjectAssetFileUtility.IsReadFailure(ex))
            {
                cancellationToken.ThrowIfCancellationRequested();
                return new([], false);
            }
        }

        public static TmbVfxReferenceCollection CollectReferences(
            byte[] data,
            CancellationToken cancellationToken = default)
            => CollectReferences(null, data, null, cancellationToken);

        public static TmbVfxReferenceCollection CollectReferences(
            IObjectAssetGameData gameData,
            byte[] data,
            CancellationToken cancellationToken = default)
            => CollectReferences(gameData, data, null, cancellationToken);

        public static bool TryCollectReferences(
            IObjectAssetGameData gameData,
            ReadOnlySpan<byte> data,
            out TmbVfxReferenceCollection collection,
            CancellationToken cancellationToken = default)
        {
            if (!TryParseTmbTimeline(data, cancellationToken, out TmbTimeline? timeline))
            {
                collection = new([], false);
                return false;
            }

            collection = CollectReferences(gameData, timeline, null, cancellationToken);
            return true;
        }

        private static TmbVfxReferenceCollection CollectReferences(
            IObjectAssetGameData? gameData,
            ReadOnlySpan<byte> data,
            string? normalizedTimelinePath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!TryParseTmbTimeline(data, cancellationToken, out TmbTimeline? timeline))
            {
                return new([], false);
            }

            return CollectReferences(gameData, timeline, normalizedTimelinePath, cancellationToken);
        }

        private static TmbVfxReferenceCollection CollectReferences(
            IObjectAssetGameData? gameData,
            TmbTimeline timeline,
            string? normalizedTimelinePath,
            CancellationToken cancellationToken)
        {
            Dictionary<string, TmbReferenceAccumulator> references = new(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, TmbTimelineNode> parsedTimelines = new(StringComparer.OrdinalIgnoreCase);
            Queue<TmbTimelineNode> pendingTimelines = new();
            TmbTimelineNode root = new(timeline);
            bool isComplete = true;

            if (normalizedTimelinePath is not null)
            {
                parsedTimelines.Add(normalizedTimelinePath, root);
            }

            QueueTimeline(root, VfxTimelineContext.None);
            while (pendingTimelines.TryDequeue(out TmbTimelineNode? node))
            {
                cancellationToken.ThrowIfCancellationRequested();
                node.IsQueued = false;
                node.HasProcessed = true;
                TmbTimeline currentTimeline = node.Timeline!;
                VfxTimelineContext context = node.Context;
                foreach (TmbPendingVfxReference reference in currentTimeline.References)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    AddReference(references, reference.Path, reference.Evidence, context | reference.ContextFlags);
                }

                foreach (string nestedTimelinePath in currentTimeline.NestedTimelinePaths)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (gameData is null)
                    {
                        isComplete = false;
                        continue;
                    }

                    if (!parsedTimelines.TryGetValue(nestedTimelinePath, out TmbTimelineNode? nestedNode))
                    {
                        try
                        {
                            ObjectAssetFileData file = ObjectAssetFileUtility.ReadGameFile(gameData, nestedTimelinePath, cancellationToken);
                            TmbTimeline? nestedTimeline = file.Data is not null
                                && TryParseTmbTimeline(file.Data, cancellationToken, out TmbTimeline? parsedTimeline)
                                    ? parsedTimeline
                                    : null;
                            nestedNode = new TmbTimelineNode(nestedTimeline, isComplete: file.Data is null ? file.IsComplete : nestedTimeline is not null);
                        }
                        catch (Exception ex) when (ObjectAssetFileUtility.IsReadFailure(ex))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            nestedNode = new TmbTimelineNode(null, isComplete: false);
                        }
                        parsedTimelines.Add(nestedTimelinePath, nestedNode);
                    }

                    if (nestedNode.Timeline is null)
                    {
                        isComplete &= nestedNode.IsComplete;
                        continue;
                    }

                    QueueTimeline(nestedNode, context | VfxTimelineContext.NestedTimeline);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            TmbVfxReference[] snapshot = references
                .OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(static pair => new TmbVfxReference(pair.Key, pair.Value.Evidence, pair.Value.ContextFlags))
                .ToArray();
            cancellationToken.ThrowIfCancellationRequested();
            return new(snapshot, isComplete);

            void QueueTimeline(TmbTimelineNode node, VfxTimelineContext inheritedContext)
            {
                VfxTimelineContext context = node.Context | inheritedContext;

                if (node.HasProcessed && context == node.Context)
                {
                    return;
                }

                node.Context = context;

                if (!node.IsQueued)
                {
                    node.IsQueued = true;
                    pendingTimelines.Enqueue(node);
                }
            }
        }

        private static bool TryParseTmbTimeline(
            ReadOnlySpan<byte> data,
            CancellationToken cancellationToken,
            [NotNullWhen(true)] out TmbTimeline? timeline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            timeline = null;

            if (data.Length < 12 || BinaryPrimitives.ReadUInt32LittleEndian(data) != MagicTMLB)
            {
                return false;
            }

            int fileSize = BinaryPrimitives.ReadInt32LittleEndian(data[4..]);
            int itemCount = BinaryPrimitives.ReadInt32LittleEndian(data[8..]);

            if (fileSize != data.Length || itemCount < 0 || itemCount > (fileSize - 12) / 8)
            {
                return false;
            }

            List<TmbPendingVfxReference> directReferences = [];
            HashSet<string> nestedTimelinePaths = new(StringComparer.OrdinalIgnoreCase);
            VfxTimelineContext fileContextFlags = VfxTimelineContext.None;
            int itemStart = 12;

            for (int i = 0; i < itemCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (itemStart > fileSize - 8)
                {
                    return false;
                }

                ReadOnlySpan<byte> entry = data[itemStart..];
                uint magic = BinaryPrimitives.ReadUInt32LittleEndian(entry);
                int size = BinaryPrimitives.ReadInt32LittleEndian(entry[4..]);
                (int minimumSize, int pathOffsetField, string suffix) = magic switch
                {
                    MagicC002 => (28, 24, ".tmb"),
                    MagicC009 => (24, 20, ".pap"),
                    MagicC010 => (40, 32, ".pap"),
                    MagicC012 => (72, 20, string.Empty),
                    MagicC063 => (32, 20, string.Empty),
                    MagicC173 => (68, 20, string.Empty),
                    _         => (8, 0, string.Empty),
                };

                if (size < minimumSize || size > entry.Length)
                {
                    return false;
                }

                entry = entry[..size];
                string path = string.Empty;

                if (pathOffsetField != 0 && !TryReadOffsetGamePath(data, itemStart,
                        BinaryPrimitives.ReadInt32LittleEndian(entry[pathOffsetField..]), suffix, cancellationToken, out path))
                {
                    return false;
                }

                if (path.Length > 0)
                {
                    switch (magic)
                    {
                        case MagicC002:
                            nestedTimelinePaths.Add(path);
                            break;
                        case MagicC009:
                        case MagicC010:
                            fileContextFlags |= VfxTimelineContext.AnimationReferenced;
                            break;
                        case MagicC063:
                            fileContextFlags |= VfxTimelineContext.SoundReferenced;
                            break;
                        case MagicC012:
                        case MagicC173:
                            directReferences.Add(ReadVfxReference(entry, path, magic == MagicC173));
                            break;
                    }
                }

                itemStart += size;
            }

            cancellationToken.ThrowIfCancellationRequested();
            timeline = new TmbTimeline(directReferences, nestedTimelinePaths.ToArray(), fileContextFlags);
            return true;
        }

        private static TmbPendingVfxReference ReadVfxReference(ReadOnlySpan<byte> entry, string path, bool isAsync)
        {
            RuntimeVfxEvidence evidence = RuntimeVfxEvidence.TimelineReferenced;

            if (isAsync)
            {
                evidence |= RuntimeVfxEvidence.AsyncTimelineReferenced;
            }
            else if ((TmbVfxVisibility)BinaryPrimitives.ReadInt32LittleEndian(entry[64..])
                is TmbVfxVisibility.DefaultWithTriggers or TmbVfxVisibility.AlwaysWithTriggers)
            {
                evidence |= RuntimeVfxEvidence.TriggerReferenced;
            }

            ReadOnlySpan<short> defaultBindPoints = isAsync ? [1, 0xFF] : [1, 0xFF, 2, 0xFF];
            VfxTimelineContext context = HasNonDefaultBindPoints(entry[24..], defaultBindPoints)
                ? VfxTimelineContext.NonDefaultBindPoints
                : VfxTimelineContext.None;
            return new(path, evidence, context);
        }

        private static void AddReference(
            IDictionary<string, TmbReferenceAccumulator> references,
            string normalizedPath,
            RuntimeVfxEvidence evidence,
            VfxTimelineContext contextFlags)
        {
            if (string.IsNullOrWhiteSpace(normalizedPath))
            {
                return;
            }

            if (references.TryGetValue(normalizedPath, out TmbReferenceAccumulator existingReference))
            {
                references[normalizedPath] = existingReference with
                {
                    Evidence = existingReference.Evidence | evidence,
                    ContextFlags = existingReference.ContextFlags | contextFlags,
                };
                return;
            }

            references.Add(normalizedPath, new TmbReferenceAccumulator(evidence, contextFlags));
        }

        private static bool TryReadOffsetGamePath(
            ReadOnlySpan<byte> data,
            int itemStart,
            int offset,
            string suffix,
            CancellationToken cancellationToken,
            out string path)
        {
            cancellationToken.ThrowIfCancellationRequested();
            path = string.Empty;

            if (offset == 0)
            {
                return true;
            }

            long targetPosition = itemStart + 8L + offset;

            if (offset < 0 || targetPosition >= data.Length)
            {
                return false;
            }

            ReadOnlySpan<byte> bytes = data[(int)targetPosition..];
            bytes = bytes[..Math.Min(bytes.Length, ObjectResourcePathEncoding.MaxGamePathByteCount)];
            int terminator = bytes.IndexOf((byte)0);
            cancellationToken.ThrowIfCancellationRequested();

            if (terminator == 0)
            {
                return true;
            }

            if (terminator < 0 || !ObjectResourcePathEncoding.TryDecodeUtf8(bytes[..terminator], out string decodedPath))
            {
                return false;
            }

            string normalizedPath = ObjectResolvedPath.FromGamePath(decodedPath).Path;
            path = normalizedPath.Length == 0 || suffix.Length == 0
                ? normalizedPath
                : ObjectResolvedPath.FromGamePath(normalizedPath + suffix).Path;
            return true;
        }

        private static bool HasNonDefaultBindPoints(ReadOnlySpan<byte> data, ReadOnlySpan<short> defaultBindPoints)
        {
            for (int i = 0; i < defaultBindPoints.Length; i++)
            {
                if (BinaryPrimitives.ReadInt16LittleEndian(data[(i * sizeof(short))..]) != defaultBindPoints[i])
                {
                    return true;
                }
            }

            return false;
        }

        [StructLayout(LayoutKind.Auto)]
        private readonly record struct TmbReferenceAccumulator(
            RuntimeVfxEvidence Evidence,
            VfxTimelineContext ContextFlags);

        [StructLayout(LayoutKind.Auto)]
        private readonly record struct TmbPendingVfxReference(
            string Path,
            RuntimeVfxEvidence Evidence,
            VfxTimelineContext ContextFlags);

        private sealed class TmbTimelineNode(TmbTimeline? timeline, bool isComplete = true)
        {
            public TmbTimeline? Timeline { get; } = timeline;
            public bool IsComplete { get; } = isComplete;
            public VfxTimelineContext Context { get; set; } = timeline?.ContextFlags ?? VfxTimelineContext.None;
            public bool HasProcessed { get; set; }
            public bool IsQueued { get; set; }
        }

        private sealed record TmbTimeline(
            IReadOnlyList<TmbPendingVfxReference> References,
            IReadOnlyList<string> NestedTimelinePaths,
            VfxTimelineContext ContextFlags);
    }
}
