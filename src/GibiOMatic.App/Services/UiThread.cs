using Avalonia.Threading;

namespace GibiOMatic.App.Services;

/// <summary>Runs view-model updates on the UI thread (background work raises events on other threads).</summary>
public interface IUiThread
{
    void Post(Action action);
}

public sealed class AvaloniaUiThread : IUiThread
{
    public void Post(Action action) => Dispatcher.UIThread.Post(action);
}

/// <summary>
/// Coalesces bursts of events (the import queue changes many times per second) into one
/// refresh, run on the UI thread at most once per <paramref name="delay"/>.
/// </summary>
public sealed class Debouncer(IUiThread ui, TimeSpan delay, Func<Task> action) : IDisposable
{
    private readonly Lock _gate = new();
    private Timer? _timer;
    private bool _disposed;

    public void Trigger()
    {
        lock (_gate)
        {
            if (_disposed || _timer is not null)
            {
                return;
            }

            _timer = new Timer(_ => Fire(), null, delay, Timeout.InfiniteTimeSpan);
        }
    }

    private void Fire()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
            if (_disposed)
            {
                return;
            }
        }

        ui.Post(async () =>
        {
            // A background refresh that fails (the library closing under it) must not take the app down.
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Background refresh failed");
            }
        });
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
    }
}
