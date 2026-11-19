namespace PPObjectSearch.Ai;

/// <summary>
/// A view model that can say more about its screen than the screen's text shows - a diagram's
/// structure, say, which on screen is boxes and lines. What it gives is sent along with the
/// screen's text when the assistant is asked about it.
/// </summary>
public interface IAiContext
{
    /// <summary>The extra content, as text; null when there is nothing to add yet.</summary>
    string? DescribeForAi();
}
