namespace GibiOMatic.Metadata.Http;

/// <summary>
/// Spaces requests to one source: at least <c>minInterval</c> between any two requests, and at
/// most <c>perHour</c> requests per resource in any sliding hour (ComicVine counts each resource
/// type separately). Callers wait instead of failing.
/// </summary>
public sealed class RateLimiter(TimeSpan minInterval, int perHour)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, Queue<DateTimeOffset>> _windows = new(StringComparer.Ordinal);
    private DateTimeOffset _last = DateTimeOffset.MinValue;

    /// <summary>Blocked until this moment (set after the server reported a rate limit).</summary>
    private DateTimeOffset _blockedUntil = DateTimeOffset.MinValue;

    public TimeSpan MinInterval { get; } = minInterval;

    public int PerHour { get; } = perHour;

    /// <summary>When the next request may start; for showing "next request in N s".</summary>
    public DateTimeOffset NextSlot { get; private set; } = DateTimeOffset.MinValue;

    public async Task WaitAsync(string resource, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            while (true)
            {
                var now = DateTimeOffset.UtcNow;
                var window = WindowOf(resource, now);
                var next = Max(_last + MinInterval, _blockedUntil);
                if (window.Count >= PerHour)
                {
                    next = Max(next, window.Peek() + TimeSpan.FromHours(1));
                }

                NextSlot = next;
                if (next <= now)
                {
                    _last = now;
                    window.Enqueue(now);
                    return;
                }

                await Task.Delay(next - now, ct);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The server said "slow down": no request to this source until <paramref name="until"/>.</summary>
    public void BlockUntil(DateTimeOffset until)
    {
        if (until > _blockedUntil)
        {
            _blockedUntil = until;
        }
    }

    private Queue<DateTimeOffset> WindowOf(string resource, DateTimeOffset now)
    {
        if (!_windows.TryGetValue(resource, out var window))
        {
            window = new Queue<DateTimeOffset>();
            _windows[resource] = window;
        }

        while (window.Count > 0 && window.Peek() <= now - TimeSpan.FromHours(1))
        {
            window.Dequeue();
        }

        return window;
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
}
