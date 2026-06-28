using Dalamud.Interface;
using System.Numerics;

namespace Intoner.Objects.UI.Settings;

internal enum SettingsTab
{
    Assets,
    Housing,
    Layouts,
    Ui,
    Drawing,
    Diagnostics,
}

internal sealed record SettingsTabDefinition(
    SettingsTab Key,
    string Label,
    string Keywords);

internal sealed record SettingDefinition(
    string Id,
    string Label,
    string Description,
    string Keywords);

/// <summary> draws one persisted object subsystem setting row </summary>
internal interface ISettingEntry
{
    /// <summary> search and display metadata for this setting </summary>
    SettingDefinition Definition { get; }

    /// <summary> layout used by settings section chrome </summary>
    SettingRowLayout Layout { get; }

    /// <summary> draws the setting row and control </summary>
    /// <param name="context"> services needed to read and update the setting </param>
    /// <param name="accent"> section accent color </param>
    /// <param name="prominentControl"> true when the row is the only setting in its section </param>
    void DrawRow(DrawContext context, Vector4 accent, bool prominentControl);
}

internal readonly record struct SettingRowLayout(float? ControlColumnWidth = null);

internal sealed record SettingsSection(
    SettingsTab Tab,
    string Id,
    FontAwesomeIcon Icon,
    string Title,
    string Description,
    string Keywords,
    IReadOnlyList<ISettingEntry> Entries);

internal sealed record SectionResult(
    SettingsSection Section,
    IReadOnlyList<ISettingEntry> Entries);

internal sealed record SearchResult(
    IReadOnlyList<SectionResult> Sections,
    int EntryCount);

internal readonly record struct SettingStatus(string Text, Vector4 Color);

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
    SettingsTab? Tab,
    string Label,
    SearchResult Result);

internal sealed record SettingsView(
    SearchQuery Query,
    SearchResult AllResult,
    SearchResult SelectedResult,
    IReadOnlyList<CategoryResult> Categories);

internal readonly record struct SearchQuery(string[] Tokens)
{
    public bool HasTokens
        => Tokens.Length > 0;
}

