namespace Intoner.Objects.Models;

/// <summary> explicit folder path and optional custom color </summary>
/// <param name="Path"> folder path relative to its owner </param>
/// <param name="Color"> custom color as #RRGGBB or #RRGGBBAA, or null for the default color </param>
internal sealed record ObjectFolderSnapshot(string Path, string? Color = null);
