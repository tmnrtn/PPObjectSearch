using System.Text.Json;

namespace PPObjectSearch.Ai;

/// <summary>What the assistant can reach beyond the screen it was asked about: the rest of the app.</summary>
public interface IAiWorkspace
{
    /// <summary>The open screens and environments, one line each, by the titles <see cref="ReadScreen"/> takes.</summary>
    string ListScreens();

    /// <summary>A screen's content, as the assistant is shown a screen; null if no screen has that title.</summary>
    AiScreen? ReadScreen(string title);

    /// <summary>Solution objects matching the words, from the named environment or the selected one.</summary>
    string SearchObjects(string query, string? environment);
}

/// <summary>The tools offered to models that can use them, all read-only.</summary>
public static class AiTools
{
    public static IReadOnlyList<AiTool> For(IAiWorkspace? workspace)
    {
        if (workspace is null) return [];

        return
        [
            new AiTool
            {
                Name = "list_open_screens",
                Description = "Lists the app's open screens (object details, Failures, Table data and the like) and " +
                              "connected environments, by title. Use it to find a screen to read.",
                Run = (_, _) => Task.FromResult(workspace.ListScreens())
            },
            new AiTool
            {
                Name = "read_screen",
                Description = "Reads one of the app's open screens or environments, by the title list_open_screens gives, " +
                              "as text in the same form as the screen you were given.",
                Properties = new Dictionary<string, JsonElement>
                {
                    ["title"] = Property("The screen's title, exactly as list_open_screens gives it.")
                },
                Required = ["title"],
                Run = (args, _) =>
                {
                    var title = AiTool.Text(args, "title") ?? string.Empty;
                    return Task.FromResult(workspace.ReadScreen(title) is { } screen
                        ? screen.Content
                        : $"No open screen is called \"{title}\". Call list_open_screens for the titles.");
                }
            },
            new AiTool
            {
                Name = "search_objects",
                Description = "Searches the solution objects loaded for an environment - tables, flows, plug-ins, web " +
                              "resources, environment variables and the rest - by keywords, all of which must match the " +
                              "name, display name, schema name, type, table, owner or id. Returns each match's type, " +
                              "names, table, state and id.",
                Properties = new Dictionary<string, JsonElement>
                {
                    ["query"] = Property("Space-separated keywords."),
                    ["environment"] = Property("The environment's title or URL; the selected environment when left out.")
                },
                Required = ["query"],
                Run = (args, _) => Task.FromResult(workspace.SearchObjects(
                    AiTool.Text(args, "query") ?? string.Empty,
                    AiTool.Text(args, "environment")))
            }
        ];
    }

    /// <summary>What a tool call is doing, in a few words, to show while it runs.</summary>
    public static string Describe(AiToolCall call) => call.Name switch
    {
        "list_open_screens" => "Looking at the open screens…",
        "read_screen" => $"Reading {AiTool.Text(call.Arguments, "title") ?? "a screen"}…",
        "search_objects" => $"Searching for {AiTool.Text(call.Arguments, "query") ?? "objects"}…",
        _ => $"Running {call.Name}…"
    };

    /// <summary>What a tool call did, once it has run, to keep beside the answer.</summary>
    public static string Done(AiToolCall call) => call.Name switch
    {
        "list_open_screens" => "Listed the open screens",
        "read_screen" => $"Read {AiTool.Text(call.Arguments, "title") ?? "a screen"}",
        "search_objects" => $"Searched objects for \"{AiTool.Text(call.Arguments, "query") ?? ""}\"",
        _ => $"Ran {call.Name}"
    };

    private static JsonElement Property(string description) =>
        JsonSerializer.SerializeToElement(new { type = "string", description });
}
