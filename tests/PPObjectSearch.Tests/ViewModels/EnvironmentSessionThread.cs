using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Windows.Threading;

namespace PPObjectSearch.Tests.ViewModels;

/// <summary>
/// Runs a test on a thread of its own that every await returns to, the way the app runs a view
/// model on its UI thread: collection views accept the changes, because they all happen on the
/// thread that made them. There is no message loop, so dispatcher timers do not tick by
/// themselves - <see cref="Elapse"/> does what a tick would.
/// </summary>
public static class EnvironmentSessionThread
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(60);

    public static Task Run(Func<Task> test)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            using var context = new PumpContext();
            SynchronizationContext.SetSynchronizationContext(context);

            Task body;
            try
            {
                body = test();
            }
            catch (Exception ex)
            {
                done.TrySetException(ex);
                return;
            }

            context.PumpUntil(body, Limit);

            if (!body.IsCompleted) done.TrySetException(new TimeoutException("The test did not finish within " + Limit + "."));
            else if (body.Exception is { } failure) done.TrySetException(failure.InnerExceptions);
            else if (body.IsCanceled) done.TrySetCanceled();
            else done.TrySetResult();
        })
        {
            IsBackground = true,
            Name = "Test UI thread"
        };

        // Windows wants STA for parts of WPF; elsewhere the call is a no-op that says no.
        thread.TrySetApartmentState(ApartmentState.STA);
        thread.Start();

        return done.Task;
    }

    /// <summary>Lets queued work run - posted continuations, progress reports - until the condition holds.</summary>
    public static async Task Until(Func<bool> condition, string what, int timeoutMs = 10_000)
    {
        var waited = Stopwatch.StartNew();
        while (!condition())
        {
            if (waited.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("Timed out waiting for " + what + ".");
            await Task.Delay(5);
        }
    }

    /// <summary>
    /// Does what a debounce timer's tick would: finds the owner's dispatcher timer with that
    /// interval and raises its Tick. Asserts the timer had been started, as the tick would not
    /// come otherwise.
    /// </summary>
    public static void Elapse(object owner, TimeSpan interval)
    {
        var timer = Timer(owner, interval);

        Assert.True(timer.IsEnabled, $"The {interval.TotalMilliseconds}ms timer was not running.");

        var tick = (EventHandler?)typeof(DispatcherTimer)
            .GetField(nameof(DispatcherTimer.Tick), BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(timer);
        tick!.Invoke(timer, EventArgs.Empty);
    }

    /// <summary>The owner's dispatcher timer with that interval - a debounce, say.</summary>
    public static DispatcherTimer Timer(object owner, TimeSpan interval) =>
        owner.GetType()
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(f => f.FieldType == typeof(DispatcherTimer))
            .Select(f => (DispatcherTimer)f.GetValue(owner)!)
            .Single(t => t.Interval == interval);

    /// <summary>A synchronization context that runs everything posted to it on the one thread that pumps it.</summary>
    private sealed class PumpContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
        private readonly int _threadId = Environment.CurrentManagedThreadId;

        public override void Post(SendOrPostCallback d, object? state)
        {
            try
            {
                _queue.Add((d, state));
            }
            catch (InvalidOperationException)
            {
                // Posted after the test finished (or disposed of the queue): nothing is left to run it.
            }
        }

        public override void Send(SendOrPostCallback d, object? state)
        {
            if (Environment.CurrentManagedThreadId == _threadId)
            {
                d(state);
                return;
            }

            using var sent = new ManualResetEventSlim();
            Post(s => { d(s); sent.Set(); }, state);
            sent.Wait();
        }

        public override SynchronizationContext CreateCopy() => this;

        public void PumpUntil(Task task, TimeSpan limit)
        {
            task.ContinueWith(_ => Post(_ => { }, null), TaskScheduler.Default);
            var waited = Stopwatch.StartNew();

            while (!task.IsCompleted && waited.Elapsed < limit)
            {
                if (_queue.TryTake(out var work, TimeSpan.FromMilliseconds(50))) work.Callback(work.State);
            }
        }

        public void Dispose()
        {
            _queue.CompleteAdding();
            _queue.Dispose();
        }
    }
}
