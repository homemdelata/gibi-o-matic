using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using GibiOMatic.App.Services;

namespace GibiOMatic.App.Tests;

/// <summary>
/// The UI thread of a test: one thread that runs what is posted to it one item at a time, in order,
/// like Avalonia's dispatcher in the app. A test that enters it (<see cref="EnterAsync"/>) keeps
/// running on it after each await, so the test, the updates the view models post and their
/// continuations never touch the view models at the same time.
/// </summary>
public sealed class TestUiThread : IUiThread, IDisposable
{
    private readonly Context _context = new();

    public void Post(Action action) => _context.Post(_ => action(), null);

    public Awaitable EnterAsync() => new(_context);

    /// <summary>An exception thrown by something posted would crash the app: the test fails too.</summary>
    public void ThrowIfFailed()
    {
        if (_context.Errors.Count > 0)
        {
            throw new AggregateException("The UI thread failed.", _context.Errors);
        }
    }

    public void Dispose() => _context.Stop();

    public readonly struct Awaitable(SynchronizationContext context) : INotifyCompletion
    {
        public Awaitable GetAwaiter() => this;

        public bool IsCompleted => SynchronizationContext.Current == context;

        // INotifyCompletion (not the unsafe variant): the test's ExecutionContext, and with it
        // TestContext.Current, flows to the UI thread.
        public void OnCompleted(Action continuation) => context.Post(_ => continuation(), null);

        public void GetResult()
        {
        }
    }

    private sealed class Context : SynchronizationContext
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = [];
        private readonly Thread _thread;

        public Context()
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "Test UI thread" };
            _thread.Start();
        }

        public ConcurrentQueue<Exception> Errors { get; } = new();

        public override void Post(SendOrPostCallback d, object? state)
        {
            try
            {
                _queue.Add((d, state));
            }
            catch (InvalidOperationException)
            {
                // Stopped: like the app after its window closed, late updates are dropped.
            }
        }

        public override void Send(SendOrPostCallback d, object? state)
        {
            if (Thread.CurrentThread == _thread)
            {
                d(state);
                return;
            }

            if (_queue.IsAddingCompleted)
            {
                return;
            }

            using var done = new ManualResetEventSlim();
            Post(_ =>
            {
                try
                {
                    d(state);
                }
                finally
                {
                    done.Set();
                }
            }, null);
            done.Wait();
        }

        public override SynchronizationContext CreateCopy() => this;

        public void Stop()
        {
            _queue.CompleteAdding();
            if (Thread.CurrentThread != _thread)
            {
                _thread.Join(TimeSpan.FromSeconds(10));
            }
        }

        private void Run()
        {
            SetSynchronizationContext(this);
            foreach (var (callback, state) in _queue.GetConsumingEnumerable())
            {
                try
                {
                    callback(state);
                }
                catch (Exception ex)
                {
                    Errors.Enqueue(ex);
                }
            }
        }
    }
}
