namespace Sweeply.Core.Tests;

public class ReminderTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
    private readonly TestFolder _t = new();

    public void Dispose() => _t.Dispose();

    [Theory]
    [InlineData(ReminderInterval.Off, 400, false)]
    [InlineData(ReminderInterval.Weekly, 6.9, false)]
    [InlineData(ReminderInterval.Weekly, 7, true)]
    [InlineData(ReminderInterval.Monthly, 29, false)]
    [InlineData(ReminderInterval.Monthly, 30, true)]
    [InlineData(ReminderInterval.Weekly, -2, true)]   // the clock was set back
    public void Is_due_after_a_week_or_a_month(ReminderInterval interval, double daysAgo, bool due) =>
        Assert.Equal(due, Reminder.IsDue(interval, Now.AddDays(-daysAgo), Now));

    [Fact]
    public void Never_due_before_the_first_look() =>
        Assert.False(Reminder.IsDue(ReminderInterval.Weekly, null, Now));

    [Fact]
    public void Counts_only_what_is_ticked_by_default_and_can_be_cleaned_now()
    {
        var old = DateTime.UtcNow.AddDays(-10);
        _t.File(@"a\one.bin", 1000, old);
        _t.File(@"a\keep.bin", 500, old);
        _t.File(@"b\two.bin", 2000, old);
        _t.File(@"c\three.bin", 4000, old);
        var categories = new[]
        {
            new CleanupCategory { Id = "a", Group = CategoryGroup.System, Roots = new[] { _t.Dir("a") } },
            new CleanupCategory { Id = "b", Group = CategoryGroup.System, Roots = new[] { _t.Dir("b") }, SelectedByDefault = false },
            new CleanupCategory { Id = "c", Group = CategoryGroup.System, Roots = new[] { _t.Dir("c") }, BlockingProcesses = new[] { "app" } },
        };

        long bytes = Reminder.CleanableBytes(categories, DateTime.UtcNow, new[] { Path.Combine(_t.Root, @"a\keep.bin") }, p => p == "app");

        Assert.Equal(1000, bytes);
        Assert.Equal(1L << 30, Reminder.ThresholdBytes);
    }
}
