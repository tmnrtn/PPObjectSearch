using PPObjectSearch.Models;

namespace PPObjectSearch.Tests.CoreAndModels;

/// <summary>The run, trace, variable and solution history rows' derived labels.</summary>
public class RunHistoryModelsTests
{
    private static ProcessRun Run(long? duration = null, string? errorCode = null, string? errorMessage = null, string? portal = null) => new()
    {
        Name = "run", Status = "Succeeded", DurationMs = duration, ErrorCode = errorCode, ErrorMessage = errorMessage, PortalUrl = portal
    };

    [Fact]
    public void Durations_read_in_the_unit_that_suits_them()
    {
        Assert.Equal(string.Empty, ProcessRun.FormatDuration(null));
        Assert.Equal("250 ms", ProcessRun.FormatDuration(250));
        Assert.Equal($"{1.5:0.0} s", ProcessRun.FormatDuration(1500));
        Assert.Equal("1:01:01", ProcessRun.FormatDuration(3_661_000));
        Assert.Equal("250 ms", Run(250).DurationLabel);
    }

    [Theory]
    [InlineData(null, null, false)]
    [InlineData("  ", null, false)]
    [InlineData("0x80040265", null, true)]
    [InlineData(null, "Something broke", true)]
    public void A_run_has_an_error_when_it_carries_a_code_or_a_message(string? code, string? message, bool expected)
    {
        Assert.Equal(expected, Run(errorCode: code, errorMessage: message).HasError);
    }

    [Fact]
    public void A_run_has_a_portal_link_only_when_one_was_built()
    {
        Assert.False(Run().HasPortalUrl);
        Assert.True(Run(portal: "https://make.powerautomate.com/x").HasPortalUrl);
    }

    private static PluginTraceEntry Trace(string typeName, string? exception = null, long? duration = null) => new()
    {
        Id = Guid.NewGuid(), TypeName = typeName, ExceptionDetails = exception, DurationMs = duration
    };

    [Fact]
    public void A_trace_with_an_exception_failed_and_one_without_completed()
    {
        var failed = Trace("Contoso.Plugins.Validate", "System.InvalidPluginExecutionException");
        var completed = Trace("Contoso.Plugins.Validate", " ", 40);

        Assert.Equal((RunOutcome.Failed, "Exception", true), (failed.Outcome, failed.OutcomeLabel, failed.HasException));
        Assert.Equal((RunOutcome.Succeeded, "Completed", false), (completed.Outcome, completed.OutcomeLabel, completed.HasException));
        Assert.Equal("40 ms", completed.DurationLabel);
    }

    [Theory]
    [InlineData("Contoso.Plugins.Validate", "Validate")]
    [InlineData("Validate", "Validate")]
    [InlineData("Contoso.Plugins.", "Contoso.Plugins.")]
    public void A_traces_short_type_name_drops_the_namespace(string typeName, string expected)
    {
        Assert.Equal(expected, Trace(typeName).ShortTypeName);
    }

    private static EnvironmentVariableInfo Variable(string? current, bool hasCurrent, string? @default) => new()
    {
        SchemaName = "new_Url", TypeLabel = "Text", CurrentValue = current, HasCurrentValue = hasCurrent, DefaultValue = @default
    };

    [Fact]
    public void A_variables_effective_value_and_its_source_follow_what_is_set()
    {
        var current = Variable("https://prod", true, "https://dev");
        var fallback = Variable(null, false, "https://dev");
        var nothing = Variable(null, false, null);

        Assert.Equal("https://prod", current.EffectiveValue);
        Assert.StartsWith("The current value", current.EffectiveSource);
        Assert.Equal("https://dev", fallback.EffectiveValue);
        Assert.StartsWith("No current value is set", fallback.EffectiveSource);
        Assert.Null(nothing.EffectiveValue);
        Assert.StartsWith("Neither a current value nor a default", nothing.EffectiveSource);
    }

    private static SolutionHistoryEntry History(int? status = 1, bool? succeeded = true, string? sub = null, bool? managed = null,
        int? seconds = null, DateTimeOffset? start = null, DateTimeOffset? end = null, string? error = null, string? exception = null) => new()
    {
        Id = Guid.NewGuid(), SolutionName = "Core", Version = "1.0", Operation = "Import", SubOperation = sub, StatusCode = status,
        Succeeded = succeeded, IsManaged = managed, TotalSeconds = seconds, StartTime = start, EndTime = end, ErrorCode = error,
        ExceptionMessage = exception, PublisherName = "Contoso"
    };

    [Theory]
    [InlineData(0, null, RunOutcome.Running, "In progress")]
    [InlineData(2, null, RunOutcome.Running, "Queued")]
    [InlineData(1, true, RunOutcome.Succeeded, "Succeeded")]
    [InlineData(1, false, RunOutcome.Failed, "Failed")]
    [InlineData(1, null, RunOutcome.Failed, "Failed")]
    public void A_history_entry_reads_its_outcome_from_status_and_result(int status, bool? succeeded, RunOutcome outcome, string label)
    {
        var entry = History(status, succeeded);

        Assert.Equal(outcome, entry.Outcome);
        Assert.Equal(label, entry.ResultLabel);
        Assert.Equal(status == 1, entry.IsFinished);
    }

    [Theory]
    [InlineData(null, "Import")]
    [InlineData("None", "Import")]
    [InlineData("Upgrade", "Import · Upgrade")]
    public void The_sub_operation_is_shown_only_where_it_adds_something(string? sub, string expected)
    {
        Assert.Equal(expected, History(sub: sub).OperationLabel);
    }

    [Theory]
    [InlineData(true, "Managed")]
    [InlineData(false, "Unmanaged")]
    [InlineData(null, "")]
    public void A_history_entry_says_whether_it_was_managed(bool? managed, string expected)
    {
        Assert.Equal(expected, History(managed: managed).ManagedLabel);
    }

    [Fact]
    public void A_history_entrys_duration_comes_from_its_total_or_its_start_and_end()
    {
        var start = new DateTimeOffset(2024, 1, 1, 10, 0, 0, TimeSpan.Zero);

        Assert.Equal("1:00:00", History(seconds: 3600).DurationLabel);
        Assert.Equal("0:02:00", History(start: start, end: start.AddMinutes(2)).DurationLabel);
        Assert.Equal(string.Empty, History(start: start).DurationLabel);
    }

    [Fact]
    public void A_history_entry_has_an_error_and_is_searchable_by_its_parts()
    {
        var failed = History(error: "0x80048d19", sub: "Upgrade");

        Assert.True(failed.HasError);
        Assert.True(History(exception: "Import failed").HasError);
        Assert.False(History().HasError);
        Assert.Equal("core 1.0 contoso  import upgrade 0x80048d19", failed.SearchText);
    }

    [Fact]
    public void The_latest_run_summary_counts_failures_among_the_runs_it_looked_at()
    {
        var older = new ProcessRun { Name = "a", Status = "Failed", Outcome = RunOutcome.Failed, StartTime = DateTimeOffset.UnixEpoch };
        var newer = new ProcessRun
        {
            Name = "b", Status = "Failed", Outcome = RunOutcome.Failed, StartTime = DateTimeOffset.UnixEpoch.AddDays(1),
            ErrorMessage = "\r\n  First line  \r\nSecond line"
        };

        var summary = LatestRunSummary.From([older, newer])!;

        Assert.Same(newer, summary.Run);
        Assert.Equal(RunOutcome.Failed, summary.Outcome);
        Assert.Equal("2 of last 2 failed", summary.FailedLabel);
        Assert.Equal("First line", summary.ErrorLine);
        Assert.Null(LatestRunSummary.From([]));
    }

    [Theory]
    [InlineData(RunOutcome.Failed, "the only run failed")]
    [InlineData(RunOutcome.Succeeded, "the only run")]
    public void A_single_run_is_described_as_the_only_one(RunOutcome outcome, string expected)
    {
        var summary = LatestRunSummary.From([new ProcessRun { Name = "a", Status = "x", Outcome = outcome }])!;

        Assert.Equal(expected, summary.FailedLabel);
        Assert.Null(summary.ErrorLine);
    }
}
