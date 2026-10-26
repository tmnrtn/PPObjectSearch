using PPObjectSearch.Core;
using PPObjectSearch.Models;

namespace PPObjectSearch.Tests.CoreAndModels;

/// <summary>A details pane's value copied by clicking it: what is copied, and what the pointer says.</summary>
public class CopyOnClickTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("  ", null)]
    [InlineData("—", null)]
    [InlineData(" — ", null)]
    [InlineData(" someone@contoso.com ", "someone@contoso.com")]
    [InlineData("d132e6f8-276c-4c35-8f83-7ca9b9a6377a", "d132e6f8-276c-4c35-8f83-7ca9b9a6377a")]
    public void Only_a_real_value_is_copied(string? text, string? expected)
    {
        Assert.Equal(expected, CopyOnClick.ValueToCopy(text));
    }

    [Fact]
    public void The_notice_names_the_value_and_shortens_a_long_one()
    {
        Assert.Equal("Copied someone@contoso.com", CopyOnClick.Notice("someone@contoso.com", copied: true, failure: null));

        var notice = CopyOnClick.Notice(new string('x', 100), copied: true, failure: null);
        Assert.Equal("Copied " + new string('x', 57) + "…", notice);
    }

    [Fact]
    public void A_failed_copy_says_why()
    {
        Assert.Equal("Could not copy - the clipboard is held by another application (OpenClipboard failed).",
            CopyOnClick.Notice("x", copied: false, failure: "OpenClipboard failed"));
        Assert.Equal("Could not copy - the clipboard is held by another application.",
            CopyOnClick.Notice("x", copied: false, failure: null));
    }

    [Fact]
    public void A_user_can_be_found_by_their_user_id()
    {
        var id = Guid.NewGuid();
        var user = new UserInfo(id, "Alice", null, null, null, null, false, 0, null, false, null, null, null, null, null);

        Assert.Contains(id.ToString(), user.SearchText);
    }
}
