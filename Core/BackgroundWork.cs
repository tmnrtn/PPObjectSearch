namespace PPObjectSearch.Core;

/// <summary>
/// Work a view model starts without waiting for it - a load kicked off by a selection, a count
/// that fills in a label later - kept track of so it can be waited for.
///
/// The app never waits; tests do. Without this they could only sleep and hope the work had
/// finished, which a busy build machine does not always oblige.
/// </summary>
public sealed class BackgroundWork
{
    private readonly List<Task> _running = new();

    /// <summary>Starts nothing - records a task already started, and hands it back.</summary>
    public Task Track(Task task)
    {
        lock (_running)
        {
            _running.RemoveAll(t => t.IsCompleted);
            if (!task.IsCompleted) _running.Add(task);
        }

        return task;
    }

    /// <summary>Completes once nothing tracked is still running, including work started meanwhile.</summary>
    public async Task WhenIdleAsync()
    {
        while (true)
        {
            Task[] pending;
            lock (_running)
            {
                _running.RemoveAll(t => t.IsCompleted);
                pending = _running.ToArray();
            }

            if (pending.Length == 0) return;

            try
            {
                await Task.WhenAll(pending);
            }
            catch
            {
                // A failed piece of work is the view model's to report; this only waits for it.
            }
        }
    }
}
