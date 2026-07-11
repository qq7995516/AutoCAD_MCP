using System.Collections.Concurrent;

namespace AutoCadMcp.Com;

/// <summary>
/// Runs all AutoCAD COM work on a dedicated STA thread.
/// AutoCAD's COM objects require STA and must not be called from MTA/thread-pool threads.
/// </summary>
public sealed class StaDispatcher : IDisposable
{
    private readonly BlockingCollection<WorkItem> _queue = new();
    private readonly Thread _thread;
    private bool _disposed;

    public StaDispatcher()
    {
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "AutoCAD-COM-STA",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var item = new WorkItem(action, null, cancellationToken);
        _queue.Add(item, cancellationToken);
        return item.Completion.Task;
    }

    public Task<T> InvokeAsync<T>(Func<T> func, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var item = new WorkItem(null, () => func()!, cancellationToken);
        _queue.Add(item, cancellationToken);
        return item.Completion.Task.ContinueWith(
            t => (T)t.Result!,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void Run()
    {
        foreach (var item in _queue.GetConsumingEnumerable())
        {
            if (item.CancellationToken.IsCancellationRequested)
            {
                item.Completion.TrySetCanceled(item.CancellationToken);
                continue;
            }

            try
            {
                if (item.Action is not null)
                {
                    item.Action();
                    item.Completion.TrySetResult(null);
                }
                else
                {
                    item.Completion.TrySetResult(item.Func!());
                }
            }
            catch (Exception ex)
            {
                item.Completion.TrySetException(ex);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _queue.CompleteAdding();
        if (!_thread.Join(TimeSpan.FromSeconds(5)))
        {
            // Best-effort shutdown; COM may still be busy.
        }

        _queue.Dispose();
    }

    private sealed class WorkItem
    {
        public WorkItem(Action? action, Func<object?>? func, CancellationToken cancellationToken)
        {
            Action = action;
            Func = func;
            CancellationToken = cancellationToken;
            Completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public Action? Action { get; }
        public Func<object?>? Func { get; }
        public CancellationToken CancellationToken { get; }
        public TaskCompletionSource<object?> Completion { get; }
    }
}
