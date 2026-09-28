namespace Sweeply.Core;

public enum CategoryGroup
{
    System,
    Browsers,
    Developer,
    Downloads,
}

/// <summary>What a category looks for under each of its roots.</summary>
public enum ItemKind
{
    /// <summary>Every direct child of the root, file or folder.</summary>
    Children,
    /// <summary>Only direct child files matching <see cref="CleanupCategory.FilePatterns"/>.</summary>
    Files,
    /// <summary>Only direct child folders.</summary>
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

    /// <summary>For <see cref="ItemKind.Files"/>: wildcard patterns such as "*.dmp".</summary>
    public IReadOnlyList<string> FilePatterns { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Only items untouched for at least this long are offered (nothing inside modified more recently).
    /// Used for temp files, which running programs may still be using.
    /// </summary>
    public TimeSpan MinimumAge { get; init; } = TimeSpan.Zero;

    /// <summary>
    /// Process names (without .exe). While any of them runs, the category is skipped entirely.
    /// </summary>
    public IReadOnlyList<string> BlockingProcesses { get; init; } = Array.Empty<string>();

    public bool SelectedByDefault { get; init; } = true;
}
