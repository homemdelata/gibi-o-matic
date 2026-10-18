using GibiOMatic.Destinations.Kindle;
using Microsoft.Extensions.Logging;

namespace GibiOMatic.App.Services;

/// <summary>
/// Notices a Kindle being connected or unplugged (MTP has no event the app can rely on, so it
/// looks every few seconds). The app only offers to send; nothing is copied without confirmation.
/// </summary>
public sealed class KindleWatcher(IKindleDevices devices, IUiThread ui, ILogger<KindleWatcher> logger) : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(3);
    private Timer? _timer;
    private int _polling;

    public bool IsSupported => devices.IsSupported;

    /// <summary>The Kindle connected now, if any.</summary>
    public KindleDeviceInfo? Device { get; private set; }

    /// <summary>Raised on the UI thread when a Kindle is connected or unplugged.</summary>
    public event EventHandler? Changed;

    public void Start()
    {
        if (IsSupported && _timer is null)
        {
            _timer = new Timer(_ => Poll(), null, TimeSpan.Zero, Interval);
        }
    }

    /// <summary>Looks for a Kindle now (the timer calls it; tests and the "Atualizar" button too).</summary>
    public void Poll()
    {
        if (Interlocked.Exchange(ref _polling, 1) == 1)
        {
            return;
        }

        try
        {
            var found = devices.Find().FirstOrDefault();
            if (found?.Id != Device?.Id)
            {
                Device = found;
                logger.LogInformation(found is null ? "Kindle unplugged" : "Kindle connected: {Name}", found?.Name);
                ui.Post(() => Changed?.Invoke(this, EventArgs.Empty));
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Looking for a Kindle failed");
        }
        finally
        {
            Volatile.Write(ref _polling, 0);
        }
    }

    /// <summary>Opens the connected Kindle (for one operation; dispose it afterwards).</summary>
    public IKindleConnection Connect() =>
        Device is { } device ? devices.Connect(device.Id) : throw new KindleDeviceException("No Kindle is connected.");

    public void Dispose() => _timer?.Dispose();
}
