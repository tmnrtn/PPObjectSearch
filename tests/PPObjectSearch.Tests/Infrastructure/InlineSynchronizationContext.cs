namespace PPObjectSearch.Tests.Infrastructure;

/// <summary>
/// Runs posted work at once, on the thread that posts it. A view model's progress reports then
/// land before the read they report on finishes, rather than on the thread pool some time later,
/// so its status line can be checked as soon as the read is awaited.
/// </summary>
public sealed class InlineSynchronizationContext : SynchronizationContext
{
    /// <summary>Makes this the current context for the rest of the calling test's synchronous part.</summary>
    public static void Install() => SetSynchronizationContext(new InlineSynchronizationContext());

    public override void Post(SendOrPostCallback d, object? state)
    {
        var previous = Current;
        SetSynchronizationContext(this);
        try
        {
            d(state);
        }
        finally
        {
            SetSynchronizationContext(previous);
        }
    }

    public override void Send(SendOrPostCallback d, object? state) => Post(d, state);

    public override SynchronizationContext CreateCopy() => this;
}
