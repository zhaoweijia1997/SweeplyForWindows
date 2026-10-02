using System.Windows.Threading;
using Sweeply.Core;
using SweeplyForWindows.Localization;
using SweeplyForWindows.ViewModels;

namespace SweeplyForWindows.Platform;

/// <summary>
/// "Clean up every day". While the app runs in the notification area it checks once an hour whether a
/// day has passed since the last automatic clean; then the view model cleans (so the history, the
/// "Never clean" list and the busy state are the same as for a clean by hand) and a notification says
/// what went to the Recycle Bin.
/// </summary>
internal sealed class AutoCleanController : IDisposable
{
    private static readonly TimeSpan FirstCheck = TimeSpan.FromMinutes(5); // not during sign-in
    private static readonly TimeSpan Every = TimeSpan.FromHours(1);

    private readonly Settings _settings;
    private readonly TrayIcon _tray;
    private readonly MainViewModel _viewModel;
    private readonly DispatcherTimer _timer = new();
    private bool _running;

    public AutoCleanController(Settings settings, TrayIcon tray, MainViewModel viewModel)
    {
        _settings = settings;
        _tray = tray;
        _viewModel = viewModel;
        _timer.Tick += async (_, _) =>
        {
            _timer.Interval = Every;
            await CheckAsync();
        };
    }

    /// <summary>A notification about an automatic clean was shown; clicking it should show the history.</summary>
    public event Action? Shown;

    /// <summary>Brings the timer in line with the current setting.</summary>
    public void Apply()
    {
        if (!_settings.AutoCleanDaily)
        {
            _timer.Stop();
            return;
        }
        if (!_timer.IsEnabled)
        {
            _timer.Interval = FirstCheck;
            _timer.Start();
        }
    }

    private async Task CheckAsync()
    {
        if (_running || !AutoClean.IsDue(_settings.AutoCleanDaily, _settings.LastAutoCleanUtc, DateTime.UtcNow)) return;
        _running = true;
        try
        {
            var outcome = await _viewModel.AutoCleanAsync();
            if (outcome is null) return; // something else was going on: try again at the next check
            _settings.LastAutoCleanUtc = DateTime.UtcNow;
            _settings.Save();
            if (outcome.MovedCount > 0)
            {
                _tray.ShowNotification(Loc.Instance["autoClean.title"],
                    Loc.Instance.Format("autoClean.body", SizeFormatter.Format(outcome.MovedBytes, Loc.Instance.Culture), outcome.MovedCount));
                Shown?.Invoke();
            }
        }
        catch (Exception)
        {
            // Whatever went wrong, the app keeps running; the next check tries again.
        }
        finally
        {
            _running = false;
        }
    }

    public void Dispose() => _timer.Stop();
}
