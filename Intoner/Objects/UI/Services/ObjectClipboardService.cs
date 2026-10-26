using Dalamud.Bindings.ImGui;
using Intoner.Objects.Api;
using Intoner.Objects.Library;
using Intoner.Objects.Models;
using Intoner.Objects.Runtime;
using Intoner.Objects.Utils;
using Intoner.Scene;
using Intoner.Services;
using System.Numerics;

namespace Intoner.Objects.UI.Services;

internal sealed record ObjectLibraryPasteResult(
    ObjectLibraryGroup Group,
    IReadOnlyList<Guid> EntryIds);

/// <summary> reads and writes typed intoner object transfers through the system clipboard </summary>
internal interface IObjectClipboardService
{
    /// <summary> copies persistent objects and groups </summary>
    bool CopyObjects(IReadOnlyList<SceneItemSnapshot> snapshots);

    /// <summary> copies one complete placed folder </summary>
    bool CopyFolder(Guid? layoutId, string folderPath, IReadOnlyList<SceneItemSnapshot> snapshots);

    /// <summary> checks whether the clipboard contains scene objects, optionally excluding only empty containers </summary>
    bool CanPasteObjects(bool requireObjects = false);

    /// <summary> reads and prepares persistent objects for the current scene and destination </summary>
    bool TryPasteObjects(
        SceneCreationContext createdIn,
        Guid? layoutId,
        IReadOnlyList<string> existingFolders,
        ObjectPasteDestination destination,
        out ObjectTransferImport import);

    /// <summary> copies one complete object library folder or prefab </summary>
    bool CopyLibraryGroup(ObjectLibraryGroup group);

    /// <summary> checks whether the clipboard contains an object library folder or prefab </summary>
    bool CanPasteLibraryGroup();

    /// <summary> imports one object library folder or prefab </summary>
    bool TryPasteLibraryGroup(out ObjectLibraryPasteResult result);

    /// <summary> copies one transform component </summary>
    bool CopyTransform(ObjectTransformPart part, Vector3 value);

    /// <summary> reads a matching transform component </summary>
    bool TryPasteTransform(ObjectTransformPart part, out Vector3 value);
}

internal sealed class ObjectClipboardService : IObjectClipboardService
{
    private readonly IClipboardTextService _clipboard;
    private readonly IObjectFolderService _folders;
    private readonly IObjectLibrary _library;

    private string? _cachedText;
    private ObjectTransferDecodeResult _cachedResult;
    private int _cachedFrame = -1;

    public ObjectClipboardService(
        IClipboardTextService clipboard,
        IObjectFolderService folders,
        IObjectLibrary library)
    {
        _clipboard = clipboard;
        _folders = folders;
        _library = library;
    }

    public bool CopyObjects(IReadOnlyList<SceneItemSnapshot> snapshots)
    {
        if (snapshots.Count == 0 || snapshots.Any(static snapshot => snapshot is not ObjectSceneItemSnapshot))
            return false;

        ObjectFolderSceneState state = _folders.CaptureSceneState();
        List<ObjectFolderSnapshot> folders = [];
        foreach (IGrouping<Guid?, SceneItemSnapshot> layout in snapshots.GroupBy(ObjectFolderUtility.GetItemLayoutId))
        {
            HashSet<string> paths = layout.SelectMany(static snapshot => ObjectFolderUtility.EnumerateFolderAncestors(
                    ObjectFolderUtility.GetItemFolderPath(snapshot), includeSelf: true))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            folders.AddRange(ObjectFolderSceneStateUtility.GetFolders(state, layout.Key)
                .Where(folder => paths.Contains(folder.Path)));
        }

        return ObjectTransferMapper.TryCreateDocument(snapshots, folders, out ObjectTransferDocument document)
            && Write(document);
    }

    public bool CopyFolder(Guid? layoutId, string folderPath, IReadOnlyList<SceneItemSnapshot> snapshots)
    {
        if (snapshots.Any(snapshot => snapshot is not ObjectSceneItemSnapshot item || item.LayoutId != layoutId))
        {
            return false;
        }

        return ObjectTransferMapper.TryCreateFolderDocument(
                snapshots,
                folderPath,
                ObjectFolderUtility.OrderFolderEntries(
                    ObjectFolderSceneStateUtility.GetFolders(_folders.CaptureSceneState(), layoutId)
                        .Concat(snapshots.Select(static snapshot => new ObjectFolderSnapshot(ObjectFolderUtility.GetItemFolderPath(snapshot))))),
                out ObjectTransferDocument document)
            && Write(document);
    }

    public bool CanPasteObjects(bool requireObjects = false)
        => Read().Document is { Kind: ObjectTransferKind.SceneObjects, SceneObjects: { } transfer }
            && (!requireObjects || transfer.Content.Objects.Count > 0);


    public bool TryPasteObjects(
        SceneCreationContext createdIn,
        Guid? layoutId,
        IReadOnlyList<string> existingFolders,
        ObjectPasteDestination destination,
        out ObjectTransferImport import)
    {
        import = null!;
        ObjectTransferDecodeResult result = Read();
        return result.Document is { } document
            && ObjectTransferMapper.TryPrepareImport(
                document,
                createdIn,
                layoutId,
                existingFolders,
                destination,
                out import);
    }

    public bool CopyLibraryGroup(ObjectLibraryGroup group)
        => ObjectLibraryTransferMapper.TryCreateDocument(
            _library.Current,
            group,
            out ObjectTransferDocument document)
        && Write(document);

    public bool CanPasteLibraryGroup()
        => Read().Document is { Kind: ObjectTransferKind.ObjectLibrary };

    public bool TryPasteLibraryGroup(out ObjectLibraryPasteResult result)
    {
        result = null!;
        ObjectTransferDecodeResult decoded = Read();
        if (decoded.Document is not { } document
            || !ObjectLibraryTransferMapper.TryPrepareImport(document, out ObjectLibraryImport import)
            || !_library.TryImport(import, null, out ObjectLibraryGroup group))
        {
            return false;
        }

        ObjectLibrarySnapshot current = _library.Current;
        IReadOnlyList<Guid> entryIds = ObjectLibraryQuery.GetGroupEntries(
                current,
                group,
                includeDescendants: true)
            .Select(static entry => entry.Id)
            .ToArray();
        result = new ObjectLibraryPasteResult(group, entryIds);
        return true;
    }

    public bool CopyTransform(ObjectTransformPart part, Vector3 value)
    {
        if (!Enum.IsDefined(part) || !NumericsUtility.IsFinite(value))
        {
            return false;
        }

        return Write(new ObjectTransferDocument(
            ObjectTransferKind.Transform,
            Transform: new ObjectTransformValue(part, ObjectApiMapper.ToObjectVector3(value))));
    }

    public bool TryPasteTransform(ObjectTransformPart part, out Vector3 value)
    {
        value = default;
        ObjectTransferDecodeResult result = Read();
        if (result.Document is not
            {
                Kind: ObjectTransferKind.Transform,
                SceneObjects: null,
                ObjectLibrary: null,
                Transform: { } transform,
            }
            || transform.Part != part)
        {
            return false;
        }

        value = ObjectApiMapper.ToVector3(transform.Value);
        return NumericsUtility.IsFinite(value);
    }

    private ObjectTransferDecodeResult Read()
    {
        int frame = ImGui.GetFrameCount();
        if (_cachedFrame == frame)
        {
            return _cachedResult;
        }

        _cachedFrame = frame;
        string text = _clipboard.ReadText();
        if (string.Equals(text, _cachedText, StringComparison.Ordinal))
        {
            return _cachedResult;
        }

        _cachedText = text;
        _cachedResult = ObjectTransferCodec.Decode(text);
        return _cachedResult;
    }

    private bool Write(ObjectTransferDocument document)
    {
        if (!ObjectTransferCodec.TryEncode(document, out string text))
        {
            return false;
        }

        _clipboard.WriteText(text);
        _cachedText = text;
        _cachedResult = new ObjectTransferDecodeResult(ObjectTransferDecodeStatus.Success, document);
        _cachedFrame = ImGui.GetFrameCount();
        return true;
    }
}
