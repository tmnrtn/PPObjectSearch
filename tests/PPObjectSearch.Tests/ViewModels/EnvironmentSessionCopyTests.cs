using PPObjectSearch.Core;
using PPObjectSearch.Models;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Tests.ViewModels;

/// <summary>Copying names, ids, links and whole rows from an environment tab - one row, or a line per selected row.</summary>
[Collection(nameof(ClipboardText))]
public sealed class EnvironmentSessionCopyTests : IDisposable
{
    private readonly Action<string> _set = ClipboardText.Set;
    private readonly List<string> _copied = new();
    private string? _busy;

    public EnvironmentSessionCopyTests()
    {
        ClipboardText.Set = text =>
        {
            if (_busy is not null) throw new InvalidOperationException(_busy);
            _copied.Add(text);
        };
    }

    public void Dispose() => ClipboardText.Set = _set;

    private static SolutionComponentItem Row(EnvironmentSessionViewModel session, string label) =>
        session.AllItems.Single(i => i.PrimaryLabel == label);

    [Fact]
    public Task One_row_copies_its_value_and_says_what_was_copied() => EnvironmentSessionThread.Run(async () =>
    {
        var (_, session) = await EnvironmentSessionFilterTests.ConnectedAsync();
        var account = Row(session, "Account");

        session.CopyNameCommand.Execute(account);
        Assert.Equal("Copied name: account", session.Status);

        session.CopyIdCommand.Execute(account);
        Assert.Equal($"Copied object id: {account.ObjectId}", session.Status);

        session.CopyLinkCommand.Execute(account);
        Assert.Equal("Copied maker portal link: " + account.MakerUrl, session.Status);

        Assert.True(session.CopyTenantIdCommand.CanExecute(null));
        session.CopyTenantIdCommand.Execute(null);
        Assert.Equal("Copied tenant id: 22222222-0000-0000-0000-000000000002", session.Status);

        Assert.Equal(["account", account.ObjectId.ToString(), account.MakerUrl!, "22222222-0000-0000-0000-000000000002"], _copied);
    });

    [Fact]
    public Task Several_rows_copy_a_line_each_and_count_those_without_one() => EnvironmentSessionThread.Run(async () =>
    {
        var (_, session) = await EnvironmentSessionFilterTests.ConnectedAsync();
        var draft = new SolutionComponentItem { Name = "draft", ComponentTypeName = "Table" };
        session.SetSelection([Row(session, "Account"), Row(session, "Contact"), draft]);

        session.CopyNameCommand.Execute(null);

        Assert.Equal("Copied 3 names.", session.Status);
        Assert.Equal(string.Join(Environment.NewLine, "account", "contact", "draft"), _copied[^1]);

        session.CopyIdCommand.Execute(null);

        Assert.Equal("Copied 2 object ids (1 had none).", session.Status);
        Assert.Equal(2, _copied[^1].Split(Environment.NewLine).Length);
    });

    [Fact]
    public Task Rows_copy_as_a_table_that_pastes_into_a_spreadsheet() => EnvironmentSessionThread.Run(async () =>
    {
        var (_, session) = await EnvironmentSessionFilterTests.ConnectedAsync();
        var odd = new SolutionComponentItem { Name = "tab\there", DisplayName = "line\r\nbreak", ComponentTypeName = "Table" };
        session.SetSelection([Row(session, "Account"), odd]);

        session.CopyAsTableCommand.Execute(null);

        Assert.Equal("Copied 2 row(s) as a table.", session.Status);
        var lines = _copied.Single().Split(Environment.NewLine);
        Assert.Equal("Name\tDisplay name\tObject type\tSub type\tState\tObject id\tMaker portal link", lines[0]);
        Assert.StartsWith("account\tAccount\tTable\t\tUnmanaged\t" + Row(session, "Account").ObjectId + "\t", lines[1]);
        Assert.Equal("tab here\tline  break\tTable\t\tUnmanaged\t\t", lines[2]);
    });

    [Fact]
    public Task A_clipboard_held_elsewhere_is_reported() => EnvironmentSessionThread.Run(async () =>
    {
        var (_, session) = await EnvironmentSessionFilterTests.ConnectedAsync();
        _busy = "OpenClipboard failed";

        session.CopyNameCommand.Execute(Row(session, "Account"));
        Assert.Equal("Could not copy the name - the clipboard is held by another application (OpenClipboard failed).", session.Status);

        session.SetSelection([Row(session, "Account"), Row(session, "Contact")]);
        session.CopyNameCommand.Execute(null);
        Assert.Equal("Could not copy - the clipboard is held by another application (OpenClipboard failed).", session.Status);

        session.CopyAsTableCommand.Execute(null);
        Assert.Equal("Could not copy - the clipboard is held by another application (OpenClipboard failed).", session.Status);
        Assert.Empty(_copied);
    });
}
