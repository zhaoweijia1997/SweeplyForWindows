using Sweeply.Core;

namespace SweeplyForWindows.ViewModels;

/// <summary>One line of the list shown before cleaning: a category heading, or one item to move.</summary>
public sealed class ReviewRow
{
    public ReviewRow(string heading) => Heading = heading;

    public ReviewRow(ItemViewModel item, CleanupCategory category)
    {
        Item = item;
        Category = category;
    }

    public string? Heading { get; }
    public ItemViewModel? Item { get; }
    public CleanupCategory? Category { get; }
    public bool IsHeading => Heading is not null;
}
