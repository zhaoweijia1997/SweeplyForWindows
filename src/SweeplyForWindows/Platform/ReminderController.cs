using System.Windows.Threading;
using Sweeply.Core;
using SweeplyForWindows.Localization;

namespace SweeplyForWindows.Platform;

/// <summary>
/// "Remind me to clean up". While the app runs in the notification area it checks once an hour
/// whether a week or a month has passed; then it looks (only reads, at low priority) and shows a
/// notification if at least <see cref="Reminder.ThresholdBytes"/> could be cleaned.
/// </summary>
internal sealed class ReminderController : IDisposable
{
    private static readonly TimeSpan FirstCheck = TimeSpan.FromMinutes(5); // not during sign-in
    private static readonly TimeSpan Every = TimeSpan.FromHours(1);

    private readonly Settings _settings;
    private readonly TrayIcon _tray;
    private readonly KnownPaths _paths;
    private readonly DispatcherTimer _timer = new();
    private bool _checking;

    public ReminderController(Settings settings, TrayIcon tray, KnownPaths paths)
    {
        _settings = settings;
        _tray = tray;
        _paths = paths;
        _timer.Tick += async (_, _) =>
        {
            _timer.Interval = Every;
            await CheckAsync();
        };
    }

    /// <summary>A reminder was shown; clicking it should show fresh results.</summary>
    public event Action? Shown;

    /// <summary>Brings the timer in line with the current setting.</summary>
    public void Apply()
    {
        if (_settings.CleanReminder == ReminderInterval.Off)
        {
            _timer.Stop();
            return;
        }
        if (_settings.LastReminderUtc is null)
        {
            _settings.LastReminderUtc = DateTime.UtcNow; // the first reminder comes one full week or month from now
            _settings.Save();
        }
        if (!_timer.IsEnabled)
        {
            _timer.Interval = FirstCheck;
            _timer.Start();
        }
    }

    private async Task CheckAsync()
    {
        if (_checking || !Reminder.IsDue(_settings.CleanReminder, _settings.LastReminderUtc, DateTime.UtcNow)) return;
        _checking = true;
        try
        {
            var excluded = _settings.ExcludedFolders.ToArray();
            var paths = _paths;
            long bytes = await RunInBackground(() => Reminder.CleanableBytes(CategoryCatalog.Create(paths), DateTime.UtcNow, excluded));
            _settings.LastReminderUtc = DateTime.UtcNow;
            _settings.Save();
            if (bytes >= Reminder.ThresholdBytes)
            {
                _tray.ShowNotification(Loc.Instance["reminder.title"],
                    Loc.Instance.Format("reminder.body", SizeFormatter.Format(bytes, Loc.Instance.Culture)));
                Shown?.Invoke();
            }
        }
        catch (Exception)
        {
            // A reminder is a nicety: whatever went wrong while looking, the app keeps running.
        }
        finally
        {
            _checking = false;
        }
    }

    /// <summary>A thread of its own at low priority, so looking never slows down what the user is doing.</summary>
    private static Task<T> RunInBackground<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { tcs.SetResult(work()); }
            catch (Exception e) { tcs.SetException(e); }
        })
        {
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
        };
        thread.Start();
        return tcs.Task;
    }

    public void Dispose() => _timer.Stop();
}
