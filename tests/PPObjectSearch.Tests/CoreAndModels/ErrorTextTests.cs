using PPObjectSearch.Models;

namespace PPObjectSearch.Tests.CoreAndModels;

/// <summary>An error message boiled down to the one line a list shows.</summary>
public class ErrorTextTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("  \r\n ", null)]
    [InlineData("\r\n  First line  \r\nSecond line", "First line")]
    [InlineData("{\r\n  \"code\": \"ActionFailed\",\r\n  \"message\": \"An action failed. No dependent actions succeeded.\"\r\n}",
        "An action failed. No dependent actions succeeded.")]
    [InlineData("{\"error\":{\"code\":\"Unauthorized\",\"message\":\"The connection expired.\\nSign in again.\"}}", "The connection expired.")]
    [InlineData("{\n  \"code\": \"Terminated\"\n}", "Terminated")]
    [InlineData("{\n  \"code\": \"X\",\n  \"message\": \"  \"\n}", "X")]
    public void A_message_reads_as_its_first_line_and_json_as_its_message(string? message, string? expected)
    {
        Assert.Equal(expected, ErrorText.FirstLine(message));
    }

    [Theory]
    [InlineData("{ not json\nSecond", "{ not json")]
    [InlineData("{\n  \"other\": 1\n}", "{")]
    [InlineData("[1, 2]", "[1, 2]")]
    public void Text_that_is_not_an_error_object_is_kept_as_it_is(string message, string expected)
    {
        Assert.Equal(expected, ErrorText.FirstLine(message));
    }

    [Fact]
    public void A_failure_without_a_message_falls_back_to_its_code()
    {
        var failure = new FailureEvent
        {
            Source = FailureSource.CloudFlow, ComponentKey = "k", ComponentName = "n", When = DateTimeOffset.UnixEpoch,
            ErrorCode = "ActionFailed", ErrorMessage = "{\n  \"message\": \"Row not found\"\n}"
        };

        Assert.Equal("Row not found", failure.ErrorLine);
        Assert.Equal("ActionFailed", new FailureEvent
        {
            Source = FailureSource.CloudFlow, ComponentKey = "k", ComponentName = "n", When = DateTimeOffset.UnixEpoch, ErrorCode = "ActionFailed"
        }.ErrorLine);
    }
}
