using System.IO;
using PPObjectSearch.Services;

namespace PPObjectSearch.Tests.CoreAndModels;

public sealed class AppSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ppos-settings-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, "settings.json");

    public AppSettingsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort: a temp folder left behind is harmless */ }
    }

    [Fact]
    public void A_missing_file_loads_defaults_without_a_problem()
    {
        var settings = AppSettings.Load(FilePath);

        Assert.Null(settings.LoadProblem);
        Assert.Null(settings.Tabs);
    }

    private static readonly string[] ReadmeProductionWrites = ["https://contoso.crm11.dynamics.com"];

    [Fact]
    public void Comments_and_trailing_commas_are_accepted_as_in_the_readme()
    {
        File.WriteAllText(FilePath, """
            {
              // Environments this app may write to.
              "AllowProductionWrites": [
                "https://contoso.crm11.dynamics.com",
              ],
              /* and a block comment */
              "ClientId": "abc",
            }
            """);

        var settings = AppSettings.Load(FilePath);

        Assert.Null(settings.LoadProblem);
        Assert.Equal("abc", settings.ClientId);
        Assert.Equal(ReadmeProductionWrites, settings.AllowProductionWrites);
    }

    [Fact]
    public void A_broken_file_is_copied_aside_and_never_overwritten()
    {
        const string broken = "{ \"ClientId\": \"abc\" \"Tabs\": [] }";
        File.WriteAllText(FilePath, broken);

        var settings = AppSettings.Load(FilePath);

        Assert.NotNull(settings.LoadProblem);
        Assert.Contains("could not be read", settings.LoadProblem);

        var copy = Assert.Single(Directory.GetFiles(_dir, "settings.json.bad-*"));
        Assert.Equal(broken, File.ReadAllText(copy));

        settings.ClientId = "replaced";
        settings.Save(FilePath);

        Assert.Equal(broken, File.ReadAllText(FilePath));
    }

    [Fact]
    public void Loading_a_broken_file_twice_copies_it_once()
    {
        File.WriteAllText(FilePath, "not json");

        AppSettings.Load(FilePath);
        AppSettings.Load(FilePath);

        Assert.Single(Directory.GetFiles(_dir, "settings.json.bad-*"));
    }

    [Fact]
    public void Save_round_trips_and_keeps_the_previous_file_as_a_backup()
    {
        var first = new AppSettings { ClientId = "one" };
        first.Save(FilePath);

        var second = AppSettings.Load(FilePath);
        Assert.Equal("one", second.ClientId);

        second.ClientId = "two";
        second.Save(FilePath);

        Assert.Equal("two", AppSettings.Load(FilePath).ClientId);
        Assert.Contains("\"one\"", File.ReadAllText(FilePath + ".bak"));
        Assert.False(File.Exists(FilePath + ".tmp"));
    }
}
