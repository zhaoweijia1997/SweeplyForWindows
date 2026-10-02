namespace Sweeply.Core;

public enum CategoryGroup
{
    System,
    Browsers,
    Chat,
    Developer,
    Apps,
    Downloads,
    /// <summary>Leftovers found in one folder chosen from the folder right-click menu.</summary>
    Folder,
    /// <summary>Files and folders picked on the Space page.</summary>
    Space,
}

/// <summary>What a category looks for under each of its roots.</summary>
public enum ItemKind
{
    /// <summary>Every direct child of the root, file or folder.</summary>
    Children,
    /// <summary>Only direct child files matching <see cref="CleanupCategory.NamePatterns"/>.</summary>
    Files,
    /// <summary>Only direct child folders, and only those matching <see cref="CleanupCategory.NamePatterns"/> if any are given.</summary>
    Folders,
}

/// <summary>
/// One kind of junk. Names and descriptions are not here: the app looks them up by <see cref="Id"/>
/// so they can be translated.
/// </summary>
public sealed class CleanupCategory
{
    public required string Id { get; init; }
    public required CategoryGroup Group { get; init; }

    /// <summary>Folders whose contents are cleaned. Missing roots are simply skipped.</summary>
    public required IReadOnlyList<string> Roots { get; init; }

    public ItemKind Kind { get; init; } = ItemKind.Children;

    /// <summary>
    /// Wildcard patterns for item names: required for <see cref="ItemKind.Files"/> ("*.dmp"),
    /// optional for <see cref="ItemKind.Folders"/> ("????-??" for month folders).
    /// </summary>
    public IReadOnlyList<string> NamePatterns { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Only items untouched for at least this long are offered (nothing inside created or modified
    /// more recently). Used for temp files, which running programs may still be using, and for chat
    /// pictures and files, where only old ones are offered.
    /// </summary>
    public TimeSpan MinimumAge { get; init; } = TimeSpan.Zero;

    /// <summary>
    /// Process names (without .exe). While any of them runs, the category is skipped entirely.
    /// </summary>
    public IReadOnlyList<string> BlockingProcesses { get; init; } = Array.Empty<string>();

    public bool SelectedByDefault { get; init; } = true;

    /// <summary>
    /// Nothing is moved that is, is inside, or contains one of these: Windows, installed programs and
    /// app data, for items picked by hand on the Space page. The usual categories need none.
    /// </summary>
    public IReadOnlyList<string> ProtectedPlaces { get; init; } = Array.Empty<string>();

    /// <summary>Folders whose contents may be moved but never the folder itself (Desktop, Documents…).</summary>
    public IReadOnlyList<string> KeptFolders { get; init; } = Array.Empty<string>();

    /// <summary>Refuses hidden system items such as pagefile.sys, System Volume Information or $Recycle.Bin.</summary>
    public bool RefuseSystemItems { get; init; }
}
