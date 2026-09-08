using Intoner.Objects.Resources;
using System.Diagnostics.CodeAnalysis;

namespace Intoner.Objects.Collections;

internal static class PenumbraModResolver
{
    private const int OptionMaskBitCount = sizeof(ulong) * 8;

    private readonly record struct SelectedOption(
        int Index,
        PenumbraModOption Option);

    private readonly record struct SelectedOptionResolution(
        IReadOnlyList<SelectedOption> SelectedOptions,
        IReadOnlyList<string> MissingOptionNames);

    private readonly record struct GroupSelection(
        bool IsValid,
        ulong SettingValue,
        IReadOnlyList<SelectedOption> SelectedOptions);

    private sealed record SettingsState(
        IReadOnlyList<GroupSelection> GroupSelections,
        IReadOnlySet<Guid> EnabledOptionIds,
        IReadOnlySet<int> AddressableGroupIndexes);

    public static IReadOnlyList<ObjectCollectionModSettingsGroup> BuildSettingsView(
        PenumbraModMetadata metadata,
        ObjectCollectionModSettings entry)
    {
        SettingsState state = ResolveSettings(metadata, entry, []);
        List<ObjectCollectionModSettingsGroup> editableGroups = [];
        for (var groupIndex = 0; groupIndex < metadata.Groups.Count; ++groupIndex)
        {
            PenumbraModGroup group = metadata.Groups[groupIndex];
            if (!state.AddressableGroupIndexes.Contains(groupIndex)
             || !TryMapEditableGroupKind(group.Type, out ObjectCollectionModSettingsGroupKind editableKind))
            {
                continue;
            }

            int optionCount = group.Options.Count;
            if (optionCount == 0)
            {
                continue;
            }

            bool groupAvailable = IsConditionMet(group.Condition, state.EnabledOptionIds);
            if (!groupAvailable && group.Layout.HasFlag(PenumbraModLayout.Hide))
            {
                continue;
            }

            GroupSelection defaultSelection = ResolveDefaultGroupSelection(group);
            GroupSelection currentSelection = state.GroupSelections[groupIndex];
            HashSet<int> defaultIndexes = defaultSelection.SelectedOptions
                .Select(static option => option.Index)
                .ToHashSet();
            HashSet<int> selectedIndexes = currentSelection.SelectedOptions
                .Select(static option => option.Index)
                .ToHashSet();

            List<ObjectCollectionModSettingsOption> options = [];
            for (var optionIndex = 0; optionIndex < optionCount; ++optionIndex)
            {
                PenumbraModOption option = group.Options[optionIndex];
                bool optionConditionMet = IsConditionMet(option.Condition, state.EnabledOptionIds);
                bool optionAvailable = groupAvailable && optionConditionMet;
                bool optionVisible = group.Type == PenumbraModGroupType.Single
                    || optionConditionMet
                    || !option.Layout.HasFlag(PenumbraModLayout.Hide);
                options.Add(new ObjectCollectionModSettingsOption(
                    option.Name,
                    option.Description,
                    option.Priority,
                    defaultIndexes.Contains(optionIndex),
                    selectedIndexes.Contains(optionIndex),
                    optionAvailable,
                    optionVisible));
            }

            if (!options.Any(static option => option.Visible))
            {
                continue;
            }

            editableGroups.Add(new ObjectCollectionModSettingsGroup
            {
                Name = group.Name,
                Description = group.Description,
                Kind = editableKind,
                HasOverride = TryGetSavedGroupOptionNames(entry, group.Name, out _),
                Available = groupAvailable,
                Options = options,
            });
        }

        return editableGroups;
    }

    public static IReadOnlyList<ObjectPathRedirection> ResolveRedirections(
        PenumbraModMetadata metadata,
        string modRootPath,
        ObjectCollectionModSettings entry,
        IReadOnlySet<string> requestedPaths,
        ICollection<string> warnings,
        CancellationToken cancellationToken)
    {
        Dictionary<string, ObjectResolvedPath> redirects = new(StringComparer.OrdinalIgnoreCase);
        string normalizedModRootPath = PenumbraModPath.NormalizeRoot(modRootPath);
        AddMissingGroupWarnings(metadata.Groups, entry, warnings);
        SettingsState state = ResolveSettings(metadata, entry, warnings);

        foreach ((PenumbraModGroup group, int groupIndex) in metadata.Groups
                     .Select(static (group, index) => (group, index))
                     .OrderByDescending(static data => data.group.Priority)
                     .ThenByDescending(static data => data.index))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsConditionMet(group.Condition, state.EnabledOptionIds))
            {
                continue;
            }

            ApplyGroup(
                group,
                state.GroupSelections[groupIndex],
                state.EnabledOptionIds,
                entry,
                requestedPaths,
                normalizedModRootPath,
                redirects,
                warnings);
        }

        ApplyData(
            metadata.DefaultData,
            requestedPaths,
            normalizedModRootPath,
            redirects,
            entry.ModDirectory,
            warnings);
        return ObjectPathRedirectionUtility.CreateStableList(
            redirects.Select(static pair => new ObjectPathRedirection(pair.Key, pair.Value)));
    }

    private static SettingsState ResolveSettings(
        PenumbraModMetadata metadata,
        ObjectCollectionModSettings entry,
        ICollection<string> warnings)
    {
        GroupSelection[] selections = new GroupSelection[metadata.Groups.Count];
        HashSet<Guid> enabledOptionIds = [];
        HashSet<int> addressableGroupIndexes = [];
        HashSet<string> addressableGroupNames = new(StringComparer.OrdinalIgnoreCase);
        for (var groupIndex = 0; groupIndex < metadata.Groups.Count; ++groupIndex)
        {
            PenumbraModGroup group = metadata.Groups[groupIndex];
            bool addressable = addressableGroupNames.Add(group.Name);
            if (addressable)
            {
                addressableGroupIndexes.Add(groupIndex);
            }

            GroupSelection selection = ResolveGroupSelection(group, entry, warnings, addressable);
            selections[groupIndex] = selection;
            foreach (PenumbraModOption option in selection.SelectedOptions.Select(static selected => selected.Option))
            {
                if (option.Id != Guid.Empty)
                {
                    enabledOptionIds.Add(option.Id);
                }
            }
        }

        return new SettingsState(selections, enabledOptionIds, addressableGroupIndexes);
    }

    private static void ApplyGroup(
        PenumbraModGroup group,
        GroupSelection selection,
        IReadOnlySet<Guid> enabledOptionIds,
        ObjectCollectionModSettings entry,
        IReadOnlySet<string> requestedPaths,
        string normalizedModRootPath,
        Dictionary<string, ObjectResolvedPath> redirects,
        ICollection<string> warnings)
    {
        switch (group.Type)
        {
            case PenumbraModGroupType.Single:
                ApplySingleGroup(
                    selection,
                    enabledOptionIds,
                    requestedPaths,
                    normalizedModRootPath,
                    redirects,
                    entry.ModDirectory,
                    warnings);
                break;
            case PenumbraModGroupType.Multi:
                ApplyMultiGroup(
                    selection,
                    enabledOptionIds,
                    requestedPaths,
                    normalizedModRootPath,
                    redirects,
                    entry.ModDirectory,
                    warnings);
                break;
            case PenumbraModGroupType.Combining:
                ApplyCombiningGroup(
                    group,
                    selection,
                    enabledOptionIds,
                    requestedPaths,
                    normalizedModRootPath,
                    redirects,
                    entry.ModDirectory,
                    warnings);
                break;
            case PenumbraModGroupType.Imc:
                warnings.Add(
                    $"Penumbra mod '{entry.ModDirectory}' group '{group.Name}' uses IMC manipulations, which object collections do not apply");
                break;
        }
    }

    private static void ApplySingleGroup(
        GroupSelection selection,
        IReadOnlySet<Guid> enabledOptionIds,
        IReadOnlySet<string> requestedPaths,
        string normalizedModRootPath,
        Dictionary<string, ObjectResolvedPath> redirects,
        string modDirectory,
        ICollection<string> warnings)
    {
        if (!selection.IsValid || selection.SelectedOptions.Count == 0)
        {
            return;
        }

        PenumbraModOption option = selection.SelectedOptions[0].Option;
        if (IsConditionMet(option.Condition, enabledOptionIds))
        {
            ApplyData(option.Data, requestedPaths, normalizedModRootPath, redirects, modDirectory, warnings);
        }
    }

    private static void ApplyMultiGroup(
        GroupSelection selection,
        IReadOnlySet<Guid> enabledOptionIds,
        IReadOnlySet<string> requestedPaths,
        string normalizedModRootPath,
        Dictionary<string, ObjectResolvedPath> redirects,
        string modDirectory,
        ICollection<string> warnings)
    {
        if (!selection.IsValid)
        {
            return;
        }

        foreach (SelectedOption selectedOption in selection.SelectedOptions
                     .Where(option => IsConditionMet(option.Option.Condition, enabledOptionIds))
                     .OrderByDescending(static option => option.Option.Priority)
                     .ThenBy(static option => option.Index))
        {
            ApplyData(
                selectedOption.Option.Data,
                requestedPaths,
                normalizedModRootPath,
                redirects,
                modDirectory,
                warnings);
        }
    }

    private static void ApplyCombiningGroup(
        PenumbraModGroup group,
        GroupSelection selection,
        IReadOnlySet<Guid> enabledOptionIds,
        IReadOnlySet<string> requestedPaths,
        string normalizedModRootPath,
        Dictionary<string, ObjectResolvedPath> redirects,
        string modDirectory,
        ICollection<string> warnings)
    {
        if (!selection.IsValid)
        {
            return;
        }

        ulong availableMask = 0;
        int optionCount = group.Options.Count;
        for (var optionIndex = 0; optionIndex < optionCount; ++optionIndex)
        {
            if (IsConditionMet(group.Options[optionIndex].Condition, enabledOptionIds))
            {
                availableMask |= 1UL << optionIndex;
            }
        }

        ulong effectiveSetting = selection.SettingValue & availableMask;
        if (effectiveSetting >= (ulong)group.Containers.Count)
        {
            return;
        }

        ApplyData(
            group.Containers[(int)effectiveSetting],
            requestedPaths,
            normalizedModRootPath,
            redirects,
            modDirectory,
            warnings);
    }

    private static GroupSelection ResolveGroupSelection(
        PenumbraModGroup group,
        ObjectCollectionModSettings entry,
        ICollection<string> warnings,
        bool useSavedSetting)
    {
        if (useSavedSetting
         && TryGetSavedGroupOptionNames(entry, group.Name, out List<string>? optionNames))
        {
            return ResolveExplicitGroupSelection(group, entry, optionNames, warnings);
        }

        return ResolveDefaultGroupSelection(group);
    }

    private static GroupSelection ResolveExplicitGroupSelection(
        PenumbraModGroup group,
        ObjectCollectionModSettings entry,
        IReadOnlyList<string> optionNames,
        ICollection<string> warnings)
    {
        SelectedOptionResolution explicitSelection = ResolveExplicitSelectedOptions(group, optionNames);
        if (explicitSelection.MissingOptionNames.Count > 0)
        {
            warnings.Add(BuildMissingOptionWarning(entry, group.Name, explicitSelection.MissingOptionNames));
            return new GroupSelection(false, 0, []);
        }

        if (explicitSelection.SelectedOptions.Count > 0)
        {
            return CreateGroupSelection(group.Type, explicitSelection.SelectedOptions);
        }

        if (group.Type is PenumbraModGroupType.Multi or PenumbraModGroupType.Combining)
        {
            return new GroupSelection(true, 0, []);
        }

        warnings.Add(
            $"Penumbra mod '{entry.ModDirectory}' group '{group.Name}' has no valid saved option selections; default group selection was ignored");
        return new GroupSelection(false, 0, []);
    }

    private static SelectedOptionResolution ResolveExplicitSelectedOptions(
        PenumbraModGroup group,
        IReadOnlyList<string> optionNames)
    {
        if (optionNames.Count == 0)
        {
            return new SelectedOptionResolution([], []);
        }

        int loadedOptionCount = group.Options.Count;
        if (UsesBitmaskSelection(group.Type))
        {
            HashSet<string> unmatchedOptionNames = new(optionNames, StringComparer.Ordinal);
            List<SelectedOption> selectedOptions = [];
            for (var optionIndex = 0; optionIndex < loadedOptionCount; ++optionIndex)
            {
                PenumbraModOption option = group.Options[optionIndex];
                if (unmatchedOptionNames.Remove(option.Name))
                {
                    selectedOptions.Add(new SelectedOption(optionIndex, option));
                }
            }

            return new SelectedOptionResolution(selectedOptions, unmatchedOptionNames.ToArray());
        }

        string selectedName = optionNames[^1];
        for (var optionIndex = 0; optionIndex < loadedOptionCount; ++optionIndex)
        {
            PenumbraModOption option = group.Options[optionIndex];
            if (string.Equals(option.Name, selectedName, StringComparison.Ordinal))
            {
                return new SelectedOptionResolution(
                    [new SelectedOption(optionIndex, option)],
                    []);
            }
        }

        return new SelectedOptionResolution([], [selectedName]);
    }

    private static GroupSelection ResolveDefaultGroupSelection(PenumbraModGroup group)
    {
        int loadedOptionCount = group.Options.Count;
        if (group.Type == PenumbraModGroupType.Combining)
        {
            int containerCount = group.Containers.Count;
            ulong settingValue = containerCount == 0
                ? 0
                : Math.Min(group.DefaultSettings, (ulong)(containerCount - 1));
            return new GroupSelection(
                containerCount > 0,
                settingValue,
                CollectSelectedOptions(group, loadedOptionCount, settingValue));
        }

        if (UsesBitmaskSelection(group.Type))
        {
            ulong settingValue = group.DefaultSettings & BuildOptionMask(loadedOptionCount);
            return new GroupSelection(
                true,
                settingValue,
                CollectSelectedOptions(group, loadedOptionCount, settingValue));
        }

        if (loadedOptionCount == 0)
        {
            return new GroupSelection(true, 0, []);
        }

        int defaultIndex = (int)Math.Min(group.DefaultSettings, (ulong)(loadedOptionCount - 1));
        SelectedOption selectedOption = new(defaultIndex, group.Options[defaultIndex]);
        return new GroupSelection(true, (ulong)defaultIndex, [selectedOption]);
    }

    private static GroupSelection CreateGroupSelection(
        PenumbraModGroupType groupType,
        IReadOnlyList<SelectedOption> selectedOptions)
    {
        ulong settingValue = UsesBitmaskSelection(groupType)
            ? BuildSelectedOptionMask(selectedOptions)
            : (ulong)selectedOptions[0].Index;
        return new GroupSelection(true, settingValue, selectedOptions);
    }

    private static void AddMissingGroupWarnings(
        IReadOnlyList<PenumbraModGroup> groups,
        ObjectCollectionModSettings entry,
        ICollection<string> warnings)
    {
        if (entry.Settings.Count == 0)
        {
            return;
        }

        HashSet<string> groupNames = groups
            .Select(static group => group.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string groupName in entry.Settings.Keys.Where(groupName => !groupNames.Contains(groupName)))
        {
            warnings.Add($"Penumbra mod '{entry.ModDirectory}' group '{groupName}' no longer exists");
        }
    }

    private static string BuildMissingOptionWarning(
        ObjectCollectionModSettings entry,
        string groupName,
        IReadOnlyList<string> missingOptionNames)
    {
        string missingOptions = string.Join(", ", missingOptionNames.OrderBy(static name => name, StringComparer.Ordinal));
        return $"Penumbra mod '{entry.ModDirectory}' group '{groupName}' has saved options that no longer exist: {missingOptions}";
    }

    private static bool TryGetSavedGroupOptionNames(
        ObjectCollectionModSettings entry,
        string groupName,
        [NotNullWhen(true)] out List<string>? optionNames)
    {
        string normalizedGroupName = CollectionModSettingsUtility.NormalizeGroupName(groupName);
        if (entry.Settings.TryGetValue(normalizedGroupName, out List<string>? exactOptions) && exactOptions is not null)
        {
            optionNames = exactOptions;
            return true;
        }

        foreach ((string savedGroupName, List<string> savedOptions) in entry.Settings)
        {
            if (string.Equals(savedGroupName, normalizedGroupName, StringComparison.OrdinalIgnoreCase))
            {
                optionNames = savedOptions;
                return true;
            }
        }

        optionNames = null;
        return false;
    }

    private static void ApplyData(
        PenumbraModData data,
        IReadOnlySet<string> requestedPaths,
        string normalizedModRootPath,
        Dictionary<string, ObjectResolvedPath> redirects,
        string modDirectory,
        ICollection<string> warnings)
    {
        if (data.HasManipulations)
        {
            string warning = $"Penumbra mod '{modDirectory}' uses selected meta manipulations, which object collections do not apply";
            if (!warnings.Contains(warning))
            {
                warnings.Add(warning);
            }
        }

        foreach (string requestedPath in requestedPaths)
        {
            if (TryCreateLocalFileRedirect(data, requestedPath, normalizedModRootPath, out ObjectResolvedPath localFile))
            {
                redirects.TryAdd(requestedPath, localFile);
            }

            if (TryCreateFileSwapRedirect(data, requestedPath, out ObjectResolvedPath fileSwap))
            {
                redirects.TryAdd(requestedPath, fileSwap);
            }
        }
    }

    private static bool TryCreateLocalFileRedirect(
        PenumbraModData data,
        string requestedPath,
        string normalizedModRootPath,
        out ObjectResolvedPath resolvedPath)
    {
        resolvedPath = default;
        if (!data.Files.TryGetValue(requestedPath, out string? relativePath))
        {
            return false;
        }

        string normalizedRelativePath = relativePath.Replace('/', Path.DirectorySeparatorChar);
        string localFilePath;
        try
        {
            localFilePath = Path.GetFullPath(Path.Combine(normalizedModRootPath, normalizedRelativePath));
        }
        catch
        {
            return false;
        }

        if (!PenumbraModPath.Contains(normalizedModRootPath, localFilePath))
        {
            return false;
        }

        resolvedPath = ObjectResolvedPath.FromLocalFile(localFilePath);
        return ObjectResourcePathUtility.IsSupportedRedirection(requestedPath, resolvedPath);
    }

    private static bool TryCreateFileSwapRedirect(
        PenumbraModData data,
        string requestedPath,
        out ObjectResolvedPath resolvedPath)
    {
        resolvedPath = default;
        return data.FileSwaps.TryGetValue(requestedPath, out string? redirectedPath)
            && ObjectResolvedPath.TryCreate(redirectedPath, out resolvedPath)
            && ObjectResourcePathUtility.IsSupportedRedirection(requestedPath, resolvedPath);
    }

    private static bool UsesBitmaskSelection(PenumbraModGroupType groupType)
        => groupType is PenumbraModGroupType.Multi
            or PenumbraModGroupType.Combining
            or PenumbraModGroupType.Imc;

    private static bool TryMapEditableGroupKind(
        PenumbraModGroupType groupType,
        out ObjectCollectionModSettingsGroupKind editableKind)
    {
        editableKind = groupType switch
        {
            PenumbraModGroupType.Single => ObjectCollectionModSettingsGroupKind.Single,
            PenumbraModGroupType.Multi => ObjectCollectionModSettingsGroupKind.Multi,
            PenumbraModGroupType.Combining => ObjectCollectionModSettingsGroupKind.Combining,
            _ => default,
        };

        return groupType is PenumbraModGroupType.Single
            or PenumbraModGroupType.Multi
            or PenumbraModGroupType.Combining;
    }

    private static ulong BuildSelectedOptionMask(IEnumerable<SelectedOption> selectedOptions)
    {
        ulong mask = 0;
        foreach (int optionIndex in selectedOptions.Select(static option => option.Index))
        {
            if ((uint)optionIndex < OptionMaskBitCount)
            {
                mask |= 1UL << optionIndex;
            }
        }

        return mask;
    }

    private static ulong BuildOptionMask(int optionCount)
        => optionCount >= OptionMaskBitCount
            ? ulong.MaxValue
            : (1UL << optionCount) - 1;

    private static List<SelectedOption> CollectSelectedOptions(
        PenumbraModGroup group,
        int loadedOptionCount,
        ulong settingValue)
    {
        List<SelectedOption> selectedOptions = [];
        int representedOptionCount = Math.Min(loadedOptionCount, OptionMaskBitCount);
        for (var optionIndex = 0; optionIndex < representedOptionCount; ++optionIndex)
        {
            if ((settingValue & (1UL << optionIndex)) != 0)
            {
                selectedOptions.Add(new SelectedOption(optionIndex, group.Options[optionIndex]));
            }
        }

        return selectedOptions;
    }

    private static bool IsConditionMet(
        PenumbraModCondition? condition,
        IReadOnlySet<Guid> enabledOptionIds)
        => condition?.Evaluate(enabledOptionIds) ?? true;
}
