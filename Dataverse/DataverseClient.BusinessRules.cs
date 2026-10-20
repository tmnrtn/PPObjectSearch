using PPObjectSearch.Models;
using PPObjectSearch.Services;

namespace PPObjectSearch.Dataverse;

public sealed partial class DataverseClient
{
    /// <summary>A business rule's Overview: where it applies, and its conditions and actions as steps.</summary>
    private async Task BusinessRuleAsync(Guid id, ComponentOverview overview, CancellationToken ct)
    {
        using var doc = await GetOneAsync(
            $"workflows({id})?$select=name,description,primaryentity,statecode,scope,processtriggerscope,_processtriggerformid_value,xaml,modifiedon",
            ct).ConfigureAwait(false);
        var rule = doc.RootElement;

        overview.Properties.Add(new("Name", JsonHelper.GetString(rule, "name")));
        overview.Properties.Add(new("Table", Shown(rule, "primaryentity")));
        overview.Properties.Add(new("Scope", BusinessRuleScope(
            JsonHelper.GetInt(rule, "processtriggerscope"),
            Shown(rule, "processtriggerscope"),
            Shown(rule, "_processtriggerformid_value"))));
        overview.Properties.Add(new("State", Label(rule, "statecode") is { Length: > 0 } state
            ? state
            : JsonHelper.GetInt(rule, "statecode") switch { 0 => "Draft", 1 => "Active", _ => null }));
        overview.Properties.Add(new("Description", JsonHelper.GetString(rule, "description")));
        overview.Properties.Add(new("Modified", Shown(rule, "modifiedon")));

        var steps = BusinessRuleReader.Read(JsonHelper.GetString(rule, "xaml"));
        if (steps.Count == 0)
        {
            overview.Problems.Add("steps: the rule's definition could not be read as steps");
            return;
        }

        overview.Tables.Add(new OverviewTable
        {
            Title = "Steps",
            Columns = ["Step"],
            Rows = steps.Select(s => (IReadOnlyList<string?>)[s.Indented]).ToList()
        });
    }

    /// <summary>
    /// A business rule's scope as the designer names it: a specific form, All Forms or Entity.
    /// processtriggerscope is 1 (Form) for a form-scoped rule - with no form meaning all of them - and 2 for Entity.
    /// </summary>
    internal static string? BusinessRuleScope(int? scope, string? scopeShown, string? form) =>
        form is { Length: > 0 } ? "Form: " + form
        : scope switch
        {
            1 => "All forms",
            2 => "Entity",
            _ => scopeShown
        };
}
