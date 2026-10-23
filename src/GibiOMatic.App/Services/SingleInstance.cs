using System.IO.Pipes;
using System.Text;

namespace GibiOMatic.App.Services;

/// <summary>
/// Only one Gibi-o-matic runs per user (SPEC §6, item 7): the library index and the inbox folder
/// must not be handled twice. A second start hands its command line to the first one, which shows
/// its window, and exits.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stop = new();
    private Task _listener = Task.CompletedTask;

    private SingleInstance(string name)
    {
        _mutex = new Mutex(initiallyOwned: true, $"Local\\{name}", out var createdNew);
        IsFirst = createdNew;
        _pipeName = $"{name}-{Environment.UserName}";
    }

    public static SingleInstance Acquire(string name = "GibiOMatic") => new(name);

    public bool IsFirst { get; }

    /// <summary>Raised on a background thread with the command line of a second start.</summary>
    public event EventHandler<IReadOnlyList<string>>? Activated;

    /// <summary>First instance: waits for later starts.</summary>
    public void Listen()
    {
        if (IsFirst)
        {
            _listener = Task.Run(() => ListenAsync(_stop.Token));
        }
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(ct);
                using var reader = new StreamReader(server, Encoding.UTF8);
                var text = await reader.ReadToEndAsync(ct);
                Activated?.Invoke(this, text.Split('\n', StringSplitOptions.RemoveEmptyEntries));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Single instance listener failed");
                await Task.Delay(TimeSpan.FromSeconds(1), ct).ContinueWith(_ => { }, TaskScheduler.Default);
            }
        }
    }

    /// <summary>Second start: asks the first instance to show itself. Returns false if it did not answer.</summary>
    public bool SignalFirst(IReadOnlyList<string> args, TimeSpan timeout)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(timeout);
            var bytes = Encoding.UTF8.GetBytes(string.Join('\n', args) + "\n");
            client.Write(bytes);
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _listener.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
        }

        try
        {
            if (IsFirst)
            {
                _mutex.ReleaseMutex();
            }
        }
        catch (ApplicationException)
        {
            // Released from another thread: closing the handle frees it anyway.
        }

        _mutex.Dispose();
        _stop.Dispose();
    }
}
