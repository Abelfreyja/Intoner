using Dalamud.Interface;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Intoner.Objects.UI.Settings;

internal sealed record SettingsTabDefinition(
    string Id,
    string Label,
    string Keywords,
    FontAwesomeIcon Icon,
    Func<Vector4> AccentResolver)
{
    public Vector4 Accent
        => AccentResolver();
}

/// <summary> contributes settings modules to the settings catalog </summary>
internal interface ISettingsProvider
{
    /// <summary> gets the settings modules owned by this provider </summary>
    IReadOnlyList<SettingsModule> Modules { get; }
}

internal sealed record SettingsModule(
    int Order,
    SettingsTabDefinition Tab,
    IReadOnlyList<SettingsSection> Sections);

internal sealed record SettingDefinition(
    string Id,
    string Label,
    string Description,
    string Keywords);

/// <summary> draws one row on the settings page </summary>
internal interface ISettingEntry
{
    /// <summary> search and display metadata for this setting </summary>
    SettingDefinition Definition { get; }

    /// <summary> determines whether the setting participates in the current view </summary>
    bool IsVisible
        => true;

    /// <summary> layout used by settings section chrome </summary>
    SettingRowLayout Layout { get; }

    /// <summary> draws the setting row and control </summary>
    /// <param name="accent"> section accent color </param>
    /// <param name="prominentControl"> true when the row is the only setting in its section </param>
    void DrawRow(Vector4 accent, bool prominentControl);
}

internal sealed class ConditionalSettingEntry(
    ISettingEntry entry,
    Func<bool> isVisible) : ISettingEntry
{
    public SettingDefinition Definition
        => entry.Definition;

    public bool IsVisible
        => isVisible();

    public SettingRowLayout Layout
        => entry.Layout;

    public void DrawRow(Vector4 accent, bool prominentControl)
        => entry.DrawRow(accent, prominentControl);
}

internal static class SettingEntryExtensions
{
    public static ISettingEntry VisibleWhen(this ISettingEntry entry, Func<bool> isVisible)
        => new ConditionalSettingEntry(entry, isVisible);
}

[StructLayout(LayoutKind.Auto)]
internal readonly record struct SettingRowLayout(
    float? ControlColumnWidth = null,
    bool FullWidth = false);

internal sealed record SettingsSection(
    string Id,
    FontAwesomeIcon Icon,
    string Title,
    string Description,
    string Keywords,
    IReadOnlyList<ISettingEntry> Entries,
    string EntryLabel = "setting",
    string EntryPluralLabel = "settings",
    bool ShowEntryCount = true);

internal sealed record SectionResult(
    SettingsTabDefinition Tab,
    SettingsSection Section,
    IReadOnlyList<ISettingEntry> Entries);

internal sealed record SearchResult(
    IReadOnlyList<SectionResult> Sections,
    int EntryCount);

[StructLayout(LayoutKind.Auto)]
internal readonly record struct SettingStatus(string Text, Vector4 Color);

[StructLayout(LayoutKind.Auto)]
internal readonly record struct IntegerSettingRange(int Minimum, int Maximum, int Step)
{
    public int StepSize
        => Math.Max(1, Step);

    public int Clamp(int value)
        => Math.Clamp(value, Minimum, Maximum);
}

internal sealed record ChoiceOption<TValue>(
    TValue Value,
    string Label,
    string Keywords,
    string Tooltip = "");

internal sealed record CategoryResult(
    SettingsTabDefinition? Tab,
    string Label,
    SearchResult Result);

internal sealed record SettingsView(
    SearchQuery Query,
    SearchResult AllResult,
    SearchResult SelectedResult,
    IReadOnlyList<CategoryResult> Categories);

[StructLayout(LayoutKind.Auto)]
internal readonly record struct SearchQuery(string[] Tokens)
{
    public bool HasTokens
        => Tokens.Length > 0;
}

