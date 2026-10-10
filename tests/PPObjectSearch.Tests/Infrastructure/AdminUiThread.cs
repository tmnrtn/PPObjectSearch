using System.Collections.Concurrent;

namespace PPObjectSearch.Tests.Infrastructure;

/// <summary>
/// Runs a test the way the app runs its view models: on one thread, with every await coming back
/// to it. A view model's collection views refuse changes from any other thread, and work that
/// hops to the thread pool (Parallel.ForEachAsync, Task.Delay) would otherwise come back on
/// whichever test worker happens to be free.
/// </summary>
public static class AdminUiThread
{
    public static void Run(Func<Task> test)
    {
        var previous = SynchronizationContext.Current;
        using var context = new SingleThreadContext();
        SynchronizationContext.SetSynchronizationContext(context);

        try
        {
            var running = test();
            running.ContinueWith(_ => context.Complete(), TaskScheduler.Default);
            context.RunUntilComplete();
            running.GetAwaiter().GetResult();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    /// <summary>Queues every continuation, and runs them one at a time on the thread that started the test.</summary>
    private sealed class SingleThreadContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
        private volatile bool _completed;

        /// <summary>Work posted once the test is over - a load it abandoned - finishes on the thread pool.</summary>
        public override void Post(SendOrPostCallback d, object? state)
        {
            if (!_completed)
            {
                try
                {
                    _queue.Add((d, state));
                    return;
                }
                catch (InvalidOperationException)
                {
                    // Completed between the check and the add.
                }
            }

            ThreadPool.QueueUserWorkItem(_ => d(state));
        }

        public override void Send(SendOrPostCallback d, object? state) => d(state);

        public void Complete()
        {
            _completed = true;
            _queue.CompleteAdding();
        }

        public void RunUntilComplete()
        {
            foreach (var (callback, state) in _queue.GetConsumingEnumerable()) callback(state);
        }

        public void Dispose() => _queue.Dispose();
    }
}
