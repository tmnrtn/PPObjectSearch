using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Windows;
using System.Windows.Data;
using PPObjectSearch.Core;
using PPObjectSearch.Models;
using PPObjectSearch.Tests.Infrastructure;

namespace PPObjectSearch.Tests.CoreAndModels;

/// <summary>The small Core helpers: bulk collections, the binding proxy, commands, converters, paging links and retries.</summary>
public class CoreHelpersTests
{
    // ---------------------------------------------------------------- collections and binding

    [Fact]
    public void Replacing_a_bulk_collection_raises_one_reset_and_the_count_change()
    {
        var collection = new BulkObservableCollection<int>([1, 2, 3]);
        var changes = new List<NotifyCollectionChangedAction>();
        var properties = new List<string?>();
        collection.CollectionChanged += (_, e) => changes.Add(e.Action);
        ((INotifyPropertyChanged)collection).PropertyChanged += (_, e) => properties.Add(e.PropertyName);

        collection.ReplaceAll(Enumerable.Range(10, 5));

        Assert.Equal([10, 11, 12, 13, 14], collection);
        Assert.Equal([NotifyCollectionChangedAction.Reset], changes);
        Assert.Equal(["Count", "Item[]"], properties);
    }

    [Fact]
    public void A_bulk_collection_cannot_be_replaced_while_a_handler_is_reacting_to_a_change()
    {
        var collection = new BulkObservableCollection<int>();
        collection.CollectionChanged += (_, _) => { };
        collection.CollectionChanged += (_, _) => collection.ReplaceAll([9]);

        Assert.Throws<InvalidOperationException>(() => collection.Add(1));
    }

    [Fact]
    public void A_binding_proxy_carries_its_data_and_clones_with_it()
    {
        var proxy = new BindingProxy { Data = "context" };

        var clone = (BindingProxy)proxy.Clone();

        Assert.Equal("context", proxy.Data);
        Assert.Equal("context", clone.Data);
        Assert.NotSame(proxy, clone);
    }

    // ---------------------------------------------------------------- commands

    [Fact]
    public async Task An_async_command_cannot_run_twice_at_once_and_says_so_while_running()
    {
        var release = new TaskCompletionSource();
        var runs = 0;
        var command = new AsyncRelayCommand(async _ =>
        {
            runs++;
            await release.Task;
        });
        var raised = 0;
        command.CanExecuteChanged += (_, _) => raised++;

        var first = command.ExecuteAsync(null);
        var duringRun = command.CanExecute(null);
        await command.ExecuteAsync(null);
        release.SetResult();
        await first;

        Assert.False(duringRun);
        Assert.True(command.CanExecute(null));
        Assert.Equal(1, runs);
        Assert.Equal(2, raised);
    }

    [Fact]
    public void Commands_ask_their_predicate_and_a_plain_execute_runs_the_handler()
    {
        object? seen = null;
        var command = new AsyncRelayCommand(p => { seen = p; return Task.CompletedTask; }, p => p is "go");
        var relay = new RelayCommand(_ => { }, p => p is 1);
        var raised = false;
        relay.CanExecuteChanged += (_, _) => raised = true;

        command.Execute("go");
        relay.RaiseCanExecuteChanged();

        Assert.Equal("go", seen);
        Assert.True(command.CanExecute("go"));
        Assert.False(command.CanExecute("stop"));
        Assert.True(relay.CanExecute(1));
        Assert.False(relay.CanExecute(2));
        Assert.True(new RelayCommand(_ => { }).CanExecute(null));
        Assert.True(raised);
    }

    [Fact]
    public void Raising_can_execute_changed_without_listeners_is_harmless()
    {
        var command = new AsyncRelayCommand(_ => Task.CompletedTask);

        command.RaiseCanExecuteChanged();
        new RelayCommand(_ => { }).RaiseCanExecuteChanged();

        Assert.True(command.CanExecute(null));
    }

    // ---------------------------------------------------------------- converters

    [Theory]
    [InlineData(PrivilegeDepth.User, "User")]
    [InlineData(PrivilegeDepth.BusinessUnit, "BU")]
    [InlineData(PrivilegeDepth.ParentChild, "Parent")]
    [InlineData(PrivilegeDepth.Organization, "Org")]
    [InlineData(PrivilegeDepth.None, "")]
    public void A_privilege_depth_is_shown_short(PrivilegeDepth depth, string expected)
    {
        var converter = new PrivilegeDepthShortConverter();

        Assert.Equal(expected, converter.Convert(depth, typeof(string), null, CultureInfo.InvariantCulture));
        Assert.Same(Binding.DoNothing, converter.ConvertBack(expected, typeof(PrivilegeDepth), null, CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData(PrivilegeDepth.User, "User", Visibility.Visible)]
    [InlineData(PrivilegeDepth.User, "Organization", Visibility.Collapsed)]
    [InlineData(null, "User", Visibility.Collapsed)]
    [InlineData(PrivilegeDepth.User, null, Visibility.Collapsed)]
    public void An_enum_shows_only_the_view_its_value_names(PrivilegeDepth? value, string? parameter, Visibility expected)
    {
        var converter = new EnumToVisibilityConverter();

        Assert.Equal(expected, converter.Convert(value, typeof(Visibility), parameter, CultureInfo.InvariantCulture));
        Assert.Same(Binding.DoNothing, converter.ConvertBack(expected, typeof(object), parameter, CultureInfo.InvariantCulture));
    }

    // ---------------------------------------------------------------- paging links

    [Theory]
    [InlineData("http://contoso.crm.dynamics.com/next", "https://contoso.crm.dynamics.com/page")]
    [InlineData("not a link", "https://contoso.crm.dynamics.com/page")]
    [InlineData("https://contoso.crm.dynamics.com/next", "not a link")]
    public void A_next_link_that_is_not_https_on_the_same_host_is_refused(string next, string current)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Links.SameHostNext(next, current, m => new InvalidOperationException(m)));

        Assert.Contains("was not followed with your credentials", ex.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void No_next_link_means_no_next_page(string? next)
    {
        Assert.Null(Links.SameHostNext(next, "https://contoso.crm.dynamics.com/page", m => new InvalidOperationException(m)));
    }

    // ---------------------------------------------------------------- retries

    private readonly List<TimeSpan> _waits = new();

    private HttpClient Client(FakeHttpHandler inner) =>
        new(new RetryHandler(inner, 3, (wait, _) =>
        {
            _waits.Add(wait);
            return Task.CompletedTask;
        }));

    [Fact]
    public async Task A_read_that_could_not_be_sent_is_tried_again()
    {
        var attempt = 0;
        var inner = new FakeHttpHandler().On(null, "", _ => attempt++ == 0 ? throw new HttpRequestException("reset") : new HttpResponseMessage(HttpStatusCode.OK));

        using var response = await Client(inner).GetAsync("https://x.test/a");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, inner.Requests.Count);
        Assert.Single(_waits);
    }

    [Fact]
    public async Task A_write_that_could_not_be_sent_is_not_repeated()
    {
        var inner = new FakeHttpHandler().On(null, "", _ => throw new HttpRequestException("reset"));

        await Assert.ThrowsAsync<HttpRequestException>(() => Client(inner).PostAsync("https://x.test/a", new StringContent("{}")));

        Assert.Single(inner.Requests);
        Assert.Empty(_waits);
    }

    [Fact]
    public async Task A_read_that_never_gets_through_fails_after_the_last_attempt()
    {
        var inner = new FakeHttpHandler().On(null, "", _ => throw new HttpRequestException("reset"));

        await Assert.ThrowsAsync<HttpRequestException>(() => Client(inner).GetAsync("https://x.test/a"));

        Assert.Equal(3, inner.Requests.Count);
        Assert.Equal(2, _waits.Count);
    }

    [Fact]
    public void A_retry_after_date_in_the_past_or_a_negative_delay_means_retry_now()
    {
        using var past = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        past.Headers.RetryAfter = new RetryConditionHeaderValue(new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero));
        using var none = new HttpResponseMessage(HttpStatusCode.TooManyRequests);

        Assert.Equal(TimeSpan.Zero, RetryHandler.RetryAfter(past));
        Assert.Null(RetryHandler.RetryAfter(none));
    }

    [Fact]
    public async Task Request_options_travel_with_a_retried_request()
    {
        var key = new HttpRequestOptionsKey<string>("trace");
        var seen = new List<string?>();
        var attempt = 0;
        var inner = new FakeHttpHandler().On(null, "", r => new HttpResponseMessage(attempt++ == 0 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://x.test/a");
        request.Options.Set(key, "abc");
        var spy = new OptionsSpy(inner, key, seen);

        using var response = await new HttpClient(new RetryHandler(spy, 3, (_, _) => Task.CompletedTask)).SendAsync(request);

        Assert.Equal(["abc", "abc"], seen);
    }

    /// <summary>Notes an option on each request it passes on.</summary>
    private sealed class OptionsSpy(HttpMessageHandler inner, HttpRequestOptionsKey<string> key, List<string?> seen) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            seen.Add(request.Options.TryGetValue(key, out var value) ? value : null);
            return base.SendAsync(request, cancellationToken);
        }
    }
}
