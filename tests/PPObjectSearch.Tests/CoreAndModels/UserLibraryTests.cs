using System.IO;
using PPObjectSearch.Services;

namespace PPObjectSearch.Tests.CoreAndModels;

public sealed class UserLibraryTests : IDisposable
{
    private const string Env = "https://contoso.crm11.dynamics.com/";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ppos-library-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, "library.json");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort: a temp folder left behind is harmless */ }
    }

    [Fact]
    public void Favourites_searches_and_recent_objects_survive_a_restart_per_environment()
    {
        var first = UserLibrary.Load(FilePath);
        var flow = Guid.NewGuid();

        first.SetFavourite(Env, new[] { flow }, favourite: true);
        first.SaveSearch(Env, new SavedSearch { Name = "Flows", SearchText = "order", Type = "Process", FavouritesOnly = true });
        first.AddRecent(Env, new RecentObject { ObjectId = flow, Label = "Order sync" });

        // Same host, written differently.
        var second = UserLibrary.Load(FilePath).For("contoso.crm11.dynamics.com");

        Assert.Contains(flow, second.Favourites);
        var search = Assert.Single(second.Searches);
        Assert.Equal(("Flows", "order", "Process", true), (search.Name, search.SearchText, search.Type, search.FavouritesOnly));
        Assert.Equal("Order sync", Assert.Single(second.Recent).Label);
        Assert.Empty(UserLibrary.Load(FilePath).For("https://other.crm.dynamics.com").Favourites);
    }

    [Fact]
    public void Recent_keeps_each_object_once_newest_first_and_only_so_many()
    {
        var library = UserLibrary.Load(FilePath);
        var ids = Enumerable.Range(0, UserLibrary.MaxRecent + 5).Select(_ => Guid.NewGuid()).ToList();

        foreach (var id in ids) library.AddRecent(Env, new RecentObject { ObjectId = id, Label = id.ToString() });
        library.AddRecent(Env, new RecentObject { ObjectId = ids[^3], Label = "again" });

        var recent = library.For(Env).Recent;
        Assert.Equal(UserLibrary.MaxRecent, recent.Count);
        Assert.Equal("again", recent[0].Label);
        Assert.Single(recent, r => r.ObjectId == ids[^3]);
    }

    [Fact]
    public void Saving_a_search_under_an_existing_name_replaces_it()
    {
        var library = UserLibrary.Load(FilePath);

        library.SaveSearch(Env, new SavedSearch { Name = "Mine", SearchText = "a" });
        library.SaveSearch(Env, new SavedSearch { Name = "mine", SearchText = "b" });

        Assert.Equal("b", Assert.Single(library.For(Env).Searches).SearchText);

        library.DeleteSearch(Env, "MINE");
        Assert.Empty(library.For(Env).Searches);
    }

    [Fact]
    public void Renaming_a_search_keeps_its_filters_resorts_and_replaces_a_namesake()
    {
        var library = UserLibrary.Load(FilePath);

        library.SaveSearch(Env, new SavedSearch { Name = "Alpha", SearchText = "a", Type = "Process" });
        library.SaveSearch(Env, new SavedSearch { Name = "Beta", SearchText = "b" });
        library.SaveSearch(Env, new SavedSearch { Name = "Zulu", SearchText = "z" });

        library.RenameSearch(Env, "alpha", "Zulu");

        var searches = UserLibrary.Load(FilePath).For(Env).Searches;
        Assert.Equal(["Beta", "Zulu"], searches.Select(s => s.Name));
        Assert.Equal("Process", searches[1].Type);

        // A name that is not there changes nothing.
        library.RenameSearch(Env, "Missing", "Other");
        Assert.Equal(2, library.For(Env).Searches.Count);
    }

    [Fact]
    public void A_library_that_will_not_read_starts_empty_and_is_kept_aside()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "{ not json");

        var library = UserLibrary.Load(FilePath);

        Assert.Empty(library.Environments);
        Assert.True(File.Exists(FilePath + ".bad"));
    }
}
