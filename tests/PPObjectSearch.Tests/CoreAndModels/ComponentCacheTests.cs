using System.IO;
using PPObjectSearch.Models;
using PPObjectSearch.Services;
using PPObjectSearch.Tests.Infrastructure;

namespace PPObjectSearch.Tests.CoreAndModels;

/// <summary>The on-disk copy of a solution's last load, per environment and solution.</summary>
public sealed class ComponentCacheTests : IDisposable
{
    private const string Env = "https://contoso.crm11.dynamics.com";
    private static readonly Guid Solution = Guid.Parse("d0000000-0000-0000-0000-000000000001");

    private static string CacheDirectory => Path.Combine(TestDataDirectory.Path, "cache");

    public ComponentCacheTests() => ComponentCache.Clear();

    public void Dispose()
    {
        if (File.Exists(CacheDirectory)) File.Delete(CacheDirectory);
        ComponentCache.Clear();
    }

    private static SolutionComponentItem Item(string name) => new()
    {
        Name = name, DisplayName = name.ToUpperInvariant(), ComponentTypeName = "Web Resource", ComponentType = 61, ObjectId = Guid.NewGuid()
    };

    [Fact]
    public void A_saved_load_comes_back_for_the_same_environment_and_solution_with_its_search_index()
    {
        var items = new[] { Item("new_a.js"), Item("new_b.js") };

        ComponentCache.Save(Env, Solution, items);
        var cached = ComponentCache.TryLoad(Env.ToUpperInvariant(), Solution);

        Assert.NotNull(cached);
        Assert.Equal(Env, cached.EnvironmentUrl);
        Assert.Equal(Solution, cached.SolutionId);
        Assert.Equal(["new_a.js", "new_b.js"], cached.Items.Select(i => i.Name));
        Assert.Equal(items[0].ObjectId, cached.Items[0].ObjectId);
        Assert.Contains("new_a.js", cached.Items[0].SearchIndex);
        Assert.Single(Directory.GetFiles(CacheDirectory));
    }

    [Fact]
    public void Another_solution_or_environment_is_a_cache_miss()
    {
        ComponentCache.Save(Env, Solution, [Item("new_a.js")]);

        Assert.Null(ComponentCache.TryLoad(Env, Guid.NewGuid()));
        Assert.Null(ComponentCache.TryLoad("https://other.crm11.dynamics.com", Solution));
    }

    [Fact]
    public void An_empty_load_is_not_worth_showing()
    {
        ComponentCache.Save(Env, Solution, []);

        Assert.Null(ComponentCache.TryLoad(Env, Solution));
    }

    [Fact]
    public void A_cache_file_that_does_not_parse_is_a_miss()
    {
        ComponentCache.Save(Env, Solution, [Item("new_a.js")]);
        File.WriteAllText(Directory.GetFiles(CacheDirectory).Single(), "{ not json");

        Assert.Null(ComponentCache.TryLoad(Env, Solution));
    }

    [Fact]
    public void Saving_again_replaces_the_earlier_copy()
    {
        ComponentCache.Save(Env, Solution, [Item("new_a.js")]);
        ComponentCache.Save(Env, Solution, [Item("new_b.js"), Item("new_c.js")]);

        Assert.Equal(["new_b.js", "new_c.js"], ComponentCache.TryLoad(Env, Solution)!.Items.Select(i => i.Name));
        Assert.Single(Directory.GetFiles(CacheDirectory));
    }

    [Fact]
    public void A_cache_that_cannot_be_written_does_not_fail_the_load()
    {
        File.WriteAllText(CacheDirectory, "a file where the folder should be");

        ComponentCache.Save(Env, Solution, [Item("new_a.js")]);

        Assert.Null(ComponentCache.TryLoad(Env, Solution));
    }

    [Fact]
    public void Clearing_removes_every_saved_load()
    {
        ComponentCache.Save(Env, Solution, [Item("new_a.js")]);

        ComponentCache.Clear();
        ComponentCache.Clear();

        Assert.False(Directory.Exists(CacheDirectory));
        Assert.Null(ComponentCache.TryLoad(Env, Solution));
    }
}
