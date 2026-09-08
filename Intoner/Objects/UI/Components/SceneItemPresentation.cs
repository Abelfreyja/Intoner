using Dalamud.Interface;
using Intoner.Displays;
using Intoner.Objects.Collections;
using Intoner.Objects.Models;
using Intoner.Objects.Utils;
using Intoner.Services.Gpu;

namespace Intoner.Objects.UI;

internal static class SceneItemPresentation
{

    internal static List<DisplaySnapshot> FilterDisplays(
        IReadOnlyList<DisplaySnapshot> displays,
        string filter)
    {
        string[] searchTokens = SearchTermUtility.BuildSearchTokens(filter);
        if (searchTokens.Length == 0)
        {
            return [.. displays];
        }

        return displays
            .Where(display => SearchTermUtility.MatchesSearchText(
                SearchTermUtility.BuildSearchText(
                [
                    display.Name,
                    display.Settings.Target.Name,
                    display.Settings.Target.Kind.ToString(),
                ]),
                searchTokens))
            .ToList();
    }

    internal static string BuildDisplayStatus(DisplaySnapshot display)
        => $"{(display.Visible ? "visible" : "hidden")} | {BuildDisplaySourceStatus(display, includeKind: false)}";

    internal static string BuildDisplaySourceStatus(DisplaySnapshot display, bool includeKind)
    {
        WindowsCaptureTargetDescriptor target = display.Settings.Target;
        if (!target.IsConfigured)
        {
            return "No capture source";
        }

        if (!includeKind)
        {
            return target.Name;
        }

        return $"{GetDisplaySourceKind(target)} | {target.Name}";
    }

    internal static string GetDisplaySourceKind(WindowsCaptureTargetDescriptor target)
        => target.Kind == WindowsCaptureTargetKind.Monitor
            ? "Monitor"
            : "Window";

    internal static string ResolveSceneLocationLabel(string? name, uint id, string fallbackPrefix)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            return name;
        }

        return id != 0 ? $"{fallbackPrefix} #{id}" : "Unknown";
    }

    internal static FontAwesomeIcon ResolveObjectKindIcon(ObjectKind kind)
        => kind switch
        {
            ObjectKind.Furniture => FontAwesomeIcon.Home,
            ObjectKind.Light     => FontAwesomeIcon.Sun,
            ObjectKind.Vfx       => FontAwesomeIcon.Magic,
            ObjectKind.BgObject  => FontAwesomeIcon.Cube,
            _                    => FontAwesomeIcon.Cube,
        };

    internal static bool MatchesObjectFilter(
        ObjectSnapshot snapshot,
        bool isActive,
        string filter,
        ObjectKind? kindFilter)
    {
        if (!MatchesObjectKindFilter(snapshot, kindFilter))
        {
            return false;
        }

        return MatchesObjectSearchFilter(snapshot, isActive, filter);
    }

    internal static bool MatchesObjectKindFilter(ObjectSnapshot snapshot, ObjectKind? kindFilter)
        => !kindFilter.HasValue || snapshot.Kind == kindFilter.Value;

    internal static bool MatchesObjectSearchFilter(ObjectSnapshot snapshot, bool isActive, string filter)
        => string.IsNullOrWhiteSpace(filter)
            || snapshot.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || snapshot.Kind.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase)
            || BuildObjectListDetail(snapshot, isActive).Contains(filter, StringComparison.OrdinalIgnoreCase);

    internal static string BuildObjectListDetail(ObjectSnapshot snapshot, bool isActive)
        => string.Join(
            " | ",
            BuildObjectListMetaSegments(snapshot, isActive)
                .Append(ObjectSnapshotUtility.GetAssetName(snapshot)));

    internal static IEnumerable<string> BuildObjectListMetaSegments(ObjectSnapshot snapshot, bool isActive)
    {
        yield return isActive ? "active" : "inactive";
        yield return snapshot.Visible ? "visible" : "hidden";

        if (snapshot.Locked)
        {
            yield return "locked";
        }

        if (!string.IsNullOrWhiteSpace(snapshot.FolderPath))
        {
            yield return $"folder {snapshot.FolderPath}";
        }
    }

    internal static bool TryResolveObjectCollectionById(
        IReadOnlyList<ObjectCollectionSnapshot> collections,
        string collectionId,
        out ObjectCollectionSnapshot resolvedCollection)
    {
        resolvedCollection = collections.FirstOrDefault(collection => string.Equals(
            collection.Record.CollectionId,
            collectionId,
            StringComparison.OrdinalIgnoreCase))!;
        return resolvedCollection is not null;
    }
}
