using System.Text.Encodings.Web;
using System.Text.Json;
using PPObjectSearch.Models;

namespace PPObjectSearch.Services;

/// <summary>
/// The settings file <c>pac solution import --settings-file</c> takes: a value for each
/// environment variable and a connection for each connection reference in the solution.
/// </summary>
public static class DeploymentSettings
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        // Values are URLs and JSON more often than not; escaping their characters only obscures them.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <param name="withValues">
    /// True fills in what this environment currently has: each variable's current value and each
    /// reference's bound connection. False leaves every value blank, as a template.
    /// </param>
    public static string Build(
        IEnumerable<EnvironmentVariableInfo> variables,
        IEnumerable<ConnectionReferenceInfo> references,
        bool withValues)
    {
        var settings = new
        {
            EnvironmentVariables = variables
                .OrderBy(v => v.SchemaName, StringComparer.OrdinalIgnoreCase)
                .Select(v => new
                {
                    v.SchemaName,
                    Value = withValues && v.HasCurrentValue ? v.CurrentValue ?? string.Empty : string.Empty
                }),
            ConnectionReferences = references
                .OrderBy(r => r.LogicalName, StringComparer.OrdinalIgnoreCase)
                .Select(r => new
                {
                    r.LogicalName,
                    ConnectionId = withValues ? r.ConnectionId ?? string.Empty : string.Empty,
                    ConnectorId = r.ConnectorId ?? string.Empty
                })
        };

        return JsonSerializer.Serialize(settings, Options);
    }

    /// <summary>What is still blank and has to be filled in by hand before importing.</summary>
    public static (int Variables, int References) Blanks(
        IEnumerable<EnvironmentVariableInfo> variables, IEnumerable<ConnectionReferenceInfo> references, bool withValues) =>
        withValues
            ? (variables.Count(v => !v.HasCurrentValue || string.IsNullOrEmpty(v.CurrentValue)),
               references.Count(r => !r.HasConnection))
            : (variables.Count(), references.Count());
}
