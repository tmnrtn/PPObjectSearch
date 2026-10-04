using PPObjectSearch.Core;

namespace PPObjectSearch.Tests.CoreAndModels;

public class BackgroundWorkTests
{
    [Fact]
    public async Task Idle_waits_for_work_started_while_it_waits()
    {
        var work = new BackgroundWork();
        var first = new TaskCompletionSource();
        var second = new TaskCompletionSource();

        _ = work.Track(first.Task);
        var idle = work.WhenIdleAsync();

        _ = work.Track(second.Task);
        first.SetResult();
        await Task.Yield();
        Assert.False(idle.IsCompleted);

        second.SetResult();
        await idle;
    }

    [Fact]
    public async Task A_failed_task_does_not_fail_the_wait()
    {
        var work = new BackgroundWork();
        _ = work.Track(Task.FromException(new InvalidOperationException("boom")));
        _ = work.Track(Task.Run(() => throw new InvalidOperationException("later")));

        await work.WhenIdleAsync();
    }
}
