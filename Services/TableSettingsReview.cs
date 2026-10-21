using PPObjectSearch.Models;

namespace PPObjectSearch.Services;

/// <summary>Which table setting the matrix shows; All packs the four into one cell.</summary>
public enum TableSettingKind
{
    All,
    Audit,
    ChangeTracking,
    SynapseLink,
    Search
}

/// <summary>How one environment's cell reads - and is coloured.</summary>
public enum SettingCellState
{
    /// <summary>Plain text, nothing to colour: a retention period, a count, the packed All cell.</summary>
    Text,
    On,
    Off,

    /// <summary>A setting that holds but has no effect here - a column audited on a table that is not.</summary>
    Inactive,

    /// <summary>Something is wrong - a link that is not keeping up, a table stuck indexing.</summary>
    Problem,

    /// <summary>The environment could not say.</summary>
    Unknown,

    /// <summary>The table or column does not exist in this environment.</summary>
    Absent
}

/// <summary>One environment's value in a row of the matrix.</summary>
public sealed record SettingCell(SettingCellState State, string Text, string? ToolTip = null)
{
    public static SettingCell Absent(string what) => new(SettingCellState.Absent, "—", $"No such {what} in this environment");
    public static SettingCell Unknown(string why) => new(SettingCellState.Unknown, "?", why);

    public static SettingCell Flag(bool? value, string? toolTip = null) => value switch
    {
        true => new(SettingCellState.On, "On", toolTip),
        false => new(SettingCellState.Off, "Off", toolTip),
        null => Unknown(toolTip ?? "Not readable here")
    };
}

/// <summary>
/// One thing compared across environments - a table, a column, an environment setting - with a
/// cell per environment, in the order the environments were read.
/// </summary>
public sealed class SettingRow
{
    /// <param name="difference">The difference as worded by the caller - the packed All cell names its settings.</param>
    public SettingRow(string key, string name, string? detail, IReadOnlyList<string> environments, IReadOnlyList<SettingCell> cells,
        string? difference = null)
    {
        Key = key;
        Name = name;
        Detail = detail;
        Cells = cells;

        var present = cells.Where(c => c.State is not (SettingCellState.Absent or SettingCellState.Unknown)).ToList();
        IsConsistent = present.Count == cells.Count && present.Select(c => c.Text).Distinct().Count() <= 1;
        HasProblem = cells.Any(c => c.State == SettingCellState.Problem);
        Difference = difference ?? Join([.. Minority(environments, cells), .. Missing(environments, cells), .. Problems(environments, cells)]);
        Exceptions = ExceptionsOf(cells);
    }

    /// <summary>
    /// The environments, by column, that the difference is about: the odd ones out among those that
    /// have the setting, and any where it is missing, unknown or wrong.
    /// </summary>
    public IReadOnlyList<int> Exceptions { get; }

    /// <summary>Set by the view, which knows the environments' types: a production one is among <see cref="Exceptions"/>.</summary>
    public bool InvolvesProduction { get; set; }

    private static IReadOnlyList<int> ExceptionsOf(IReadOnlyList<SettingCell> cells)
    {
        var present = cells.Where(c => c.State is not (SettingCellState.Absent or SettingCellState.Unknown)).ToList();
        var usual = present.GroupBy(c => c.Text).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;
        var differs = present.Select(c => c.Text).Distinct().Count() > 1;

        return cells
            .Select((cell, i) => (cell, i))
            .Where(x => x.cell.State is SettingCellState.Absent or SettingCellState.Unknown or SettingCellState.Problem ||
                        (differs && x.cell.Text != usual))
            .Select(x => x.i)
            .ToList();
    }

    /// <summary>The table, or table|column - what an export is keyed on.</summary>
    public string Key { get; }

    /// <summary>The first column: table or column logical name, or the setting's name.</summary>
    public string Name { get; }

    /// <summary>The display name, or the table a column belongs to.</summary>
    public string? Detail { get; }

    public IReadOnlyList<SettingCell> Cells { get; }

    /// <summary>Every environment has it, and they all say the same.</summary>
    public bool IsConsistent { get; }

    public bool HasProblem { get; }

    /// <summary>
    /// "Off in QA/Prod; missing from UAT" - the exception worth chasing, without reading every
    /// column - and anything wrong even where the environments agree.
    /// </summary>
    public string Difference { get; }

    internal static string Join(IEnumerable<string> parts) => string.Join("; ", parts.Distinct());

    /// <summary>
    /// The minority is what is reported: when four environments audit a table and one does not,
    /// "Off in Prod" is the finding, not "On in Dev/QA/UAT/PreProd".
    /// </summary>
    internal static IEnumerable<string> Minority(IReadOnlyList<string> environments, IReadOnlyList<SettingCell> cells) =>
        cells
            .Select((cell, i) => (Cell: cell, Environment: environments[i]))
            .Where(c => c.Cell.State is not (SettingCellState.Absent or SettingCellState.Unknown))
            .GroupBy(c => c.Cell.Text)
            .OrderByDescending(g => g.Count())
            .Skip(1)
            .Select(g => $"{g.Key} in {string.Join("/", g.Select(c => c.Environment))}");

    /// <summary>Where the value could not be read, and where the table or column does not exist.</summary>
    internal static IEnumerable<string> Missing(IReadOnlyList<string> environments, IReadOnlyList<SettingCell> cells)
    {
        var unknown = Where(environments, cells, SettingCellState.Unknown);
        if (unknown.Count > 0) yield return $"unknown in {string.Join("/", unknown)}";

        var absent = Where(environments, cells, SettingCellState.Absent);
        if (absent.Count > 0) yield return $"missing from {string.Join("/", absent)}";
    }

    /// <summary>What is wrong, by the first line of each problem cell's explanation.</summary>
    internal static IEnumerable<string> Problems(IReadOnlyList<string> environments, IReadOnlyList<SettingCell> cells) =>
        cells
            .Select((cell, i) => (Cell: cell, Environment: environments[i]))
            .Where(c => c.Cell is { State: SettingCellState.Problem, ToolTip.Length: > 0 })
            .GroupBy(c => c.Cell.ToolTip!.Split('\n')[0])
            .Select(g => $"{char.ToLowerInvariant(g.Key[0])}{g.Key[1..]} in {string.Join("/", g.Select(c => c.Environment))}");

    private static List<string> Where(IReadOnlyList<string> environments, IReadOnlyList<SettingCell> cells, SettingCellState state) =>
        cells.Select((c, i) => (c, i)).Where(x => x.c.State == state).Select(x => environments[x.i]).ToList();
}

/// <summary>A column that is not audited on a table that is, in one environment: changes going unrecorded.</summary>
public sealed record AuditGap(string Environment, string Table, string? TableLabel, string Column, string? ColumnLabel, bool IsManaged);

/// <summary>A table with columns going unaudited somewhere: the group they are listed under.</summary>
public sealed class GapGroup
{
    public required string Table { get; init; }
    public required string Label { get; init; }
    public required int ColumnCount { get; init; }
    public required IReadOnlyList<string> Environments { get; init; }

    /// <summary>"3 columns not audited in Prod, UAT".</summary>
    public string Summary =>
        $"{ColumnCount:N0} column{(ColumnCount == 1 ? string.Empty : "s")} not audited in {string.Join(", ", Environments)}";
}

/// <summary>A column not audited somewhere on an audited table, with a cell per environment.</summary>
public sealed record GapRow(GapGroup Group, string Column, string? ColumnLabel, bool IsManaged, IReadOnlyList<SettingCell> Cells);

/// <summary>One environment's link profile: the group its tables are listed under.</summary>
public sealed class LinkGroup
{
    public required string Environment { get; init; }
    public required SynapseLinkProfile Profile { get; init; }

    /// <summary>Links to this profile's folder count as "linked".</summary>
    public required bool IsCounted { get; init; }

    public required int TableCount { get; init; }
    public required int ProblemCount { get; init; }

    /// <summary>For the environment's colour; set by the view, which knows the environments' types.</summary>
    public Dataverse.EnvironmentSku Sku { get; set; } = Dataverse.EnvironmentSku.Unknown;

    public string StateLabel => (Profile.State ?? "State unknown") + (IsCounted ? string.Empty : " · not counted");
    public string Summary => $"{TableCount:N0} table{(TableCount == 1 ? string.Empty : "s")}";
    public string ProblemLabel => ProblemCount switch { 0 => string.Empty, 1 => "1 problem", var n => $"{n:N0} problems" };
}

/// <summary>One table in one link profile, in one environment.</summary>
public sealed record LinkRow(
    string Environment,
    SynapseLinkProfile Profile,
    SynapseLinkTable Table,
    bool IsCounted)
{
    /// <summary>The environment and profile it is grouped under.</summary>
    public LinkGroup? Group { get; init; }

    /// <summary>The table's display name, else its logical name.</summary>
    public string? TableLabel { get; init; }

    /// <summary>Rows Dataverse has that the lake does not yet.</summary>
    public long? Behind => Table.SourceRecordCount is { } source && Table.LakeRecordCount is { } lake ? source - lake : null;

    public bool IsBehind => Behind > 0;

    public string BehindLabel => IsBehind ? Behind!.Value.ToString("N0") : "—";

    /// <summary>Why this row needs a look, or empty when it is keeping up.</summary>
    public string Problem => (Profile.IsActive, Table.IsActive, Table.IsHealthy) switch
    {
        (false, _, _) => $"Profile is {Profile.State ?? "not active"}",
        (_, false, _) => "Table is inactive in the profile",
        (_, _, false) => $"Sync is {Table.SyncState ?? "unknown"}",
        _ when Table.LakeRecordCount is { } lake && Table.SourceRecordCount is { } source && lake != source =>
            $"Lake has {lake:N0} rows, Dataverse {source:N0}",
        _ => string.Empty
    };

    public bool HasProblem => Problem.Length > 0;
}

/// <summary>One table's place in one environment's search index - or its absence from it.</summary>
public sealed record SearchIndexRow(string Environment, string Table, string? TableLabel, bool? IsEnabled, SearchTableStatus? Indexed)
{
    public string StatusLabel => Indexed?.StatusLabel ?? "Not indexed";
    public DateTimeOffset? LastSynced => Indexed?.LastDataSync;
    public int ColumnCount => Indexed?.IndexedColumns.Count ?? 0;
    public string ColumnsLabel => Indexed is null ? string.Empty : string.Join(", ", Indexed.IndexedColumns);

    /// <summary>Why this row needs a look, or empty when the index matches the configuration.</summary>
    public string Problem => (IsEnabled == true, Indexed) switch
    {
        (true, null) => "Search is on for the table, but it is not in the index",
        (false, not null) => "Search is off for the table, but it is still in the index",
        (_, { IsComplete: false } indexed) => $"Sync is {indexed.StatusLabel.ToLowerInvariant()}",
        _ => string.Empty
    };

    public bool HasProblem => Problem.Length > 0;
}

/// <summary>
/// Everything read from one environment for the review. A part that could not be read is null,
/// with the reason in <see cref="Problems"/> - "Synapse Link could not be read" must never look
/// like "nothing is linked".
/// </summary>
public sealed class TableSettingsSnapshot
{
    public required string Environment { get; init; }

    public IReadOnlyDictionary<string, TableSetting> Tables { get; init; } =
        new Dictionary<string, TableSetting>(StringComparer.OrdinalIgnoreCase);

    /// <summary>False when the table metadata could not be read - every table is then unknown, not missing.</summary>
    public bool TablesRead { get; init; } = true;

    public SynapseLinkSetup? Synapse { get; init; }
    public EnvironmentDataSettings? Settings { get; init; }
    public SearchIndexStatus? Search { get; init; }

    /// <summary>Column audit settings, by table - only for the tables the column comparison covers.</summary>
    public Dictionary<string, IReadOnlyList<ColumnAudit>> Columns { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Problems { get; } = new();

    /// <summary>
    /// Whether a table is exported by an active link that writes to a counted folder. Null when the
    /// links could not be read at all.
    /// </summary>
    public bool? IsLinked(string table, ISet<string> excludedFolders)
    {
        if (Synapse is null) return null;

        var counted = Synapse.Profiles
            .Where(p => p.IsActive && !excludedFolders.Contains(p.FolderLabel))
            .Select(p => p.Id)
            .ToHashSet();

        return Synapse.Tables.Any(t => t.IsActive && counted.Contains(t.ProfileId) &&
                                       string.Equals(t.Table, table, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// Compares how tables are set up for audit, change tracking, Synapse Link and Dataverse search
/// across environments - the cross-environment report the CheckSynapse and AuditSetupCheck scripts
/// produced, built from snapshots so each view can be re-cut without reading again.
/// </summary>
public static class TableSettingsReview
{
    /// <summary>
    /// One row per table in scope, one cell per environment for the chosen setting. A table missing
    /// from an environment is a finding in its own right - "not deployed" is a different problem
    /// from "audit off" - so it shows as such rather than as Off.
    /// </summary>
    public static IReadOnlyList<SettingRow> Tables(
        IReadOnlyList<TableSettingsSnapshot> snapshots,
        IEnumerable<string> scope,
        TableSettingKind kind,
        ISet<string> excludedFolders)
    {
        var environments = snapshots.Select(s => s.Environment).ToList();

        return scope
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(table =>
            {
                var cells = snapshots.Select(s => CellOf(s, table, kind, excludedFolders, "The tables could not be read here")).ToList();

                var label = snapshots.Select(s => s.Tables.GetValueOrDefault(table)?.DisplayName)
                    .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));

                var difference = kind == TableSettingKind.All
                    ? PackedDifference(snapshots, table, environments, cells, excludedFolders)
                    : null;

                return new SettingRow(table, table, label, environments, cells, difference);
            })
            .ToList();
    }

    private static readonly (TableSettingKind Kind, string Name)[] Packed =
    [
        (TableSettingKind.Audit, "audit"),
        (TableSettingKind.ChangeTracking, "change tracking"),
        (TableSettingKind.SynapseLink, "link"),
        (TableSettingKind.Search, "search")
    ];

    /// <summary>
    /// The packed cell's difference, setting by setting - "audit Off in Prod; search Off in Prod" -
    /// since "· C L · in Prod" says which letters differ but not what they mean.
    /// </summary>
    private static string PackedDifference(IReadOnlyList<TableSettingsSnapshot> snapshots, string table,
        IReadOnlyList<string> environments, IReadOnlyList<SettingCell> cells, ISet<string> excludedFolders)
    {
        var parts = new List<string>();

        foreach (var (kind, name) in Packed)
        {
            var setting = snapshots.Select(s => CellOf(s, table, kind, excludedFolders, "")).ToList();

            parts.AddRange(SettingRow.Minority(environments, setting).Select(p => $"{name} {p}"));
            parts.AddRange(SettingRow.Problems(environments, setting));
        }

        parts.AddRange(SettingRow.Missing(environments, cells));
        return SettingRow.Join(parts);
    }

    /// <summary>A table's setting in one environment: unknown where its tables were not read, absent where it has no such table.</summary>
    private static SettingCell CellOf(TableSettingsSnapshot snapshot, string table, TableSettingKind kind, ISet<string> excludedFolders, string unread)
    {
        if (!snapshot.TablesRead) return SettingCell.Unknown(unread);
        return snapshot.Tables.TryGetValue(table, out var setting) ? TableCell(snapshot, setting, kind, excludedFolders) : SettingCell.Absent("table");
    }

    private static SettingCell TableCell(TableSettingsSnapshot snapshot, TableSetting table, TableSettingKind kind, ISet<string> excludedFolders)
    {
        var environmentAudit = snapshot.Settings?.AuditEnabled;

        switch (kind)
        {
            case TableSettingKind.Audit:
                // A table's audit flag does nothing while the environment's own switch is off.
                return environmentAudit == false && table.IsAuditEnabled == true
                    ? new SettingCell(SettingCellState.Inactive, "On", "Audited, but auditing is off for the environment")
                    : SettingCell.Flag(table.IsAuditEnabled);

            case TableSettingKind.ChangeTracking:
                return SettingCell.Flag(table.ChangeTrackingEnabled);

            case TableSettingKind.SynapseLink:
                var linked = snapshot.IsLinked(table.LogicalName, excludedFolders);
                if (linked is null) return SettingCell.Unknown("Synapse Link could not be read");
                if (linked == true && table.ChangeTrackingEnabled == false)
                {
                    return new SettingCell(SettingCellState.Problem, "On", "Linked, but change tracking is off");
                }
                return SettingCell.Flag(linked, LinkToolTip(snapshot, table.LogicalName));

            case TableSettingKind.Search:
                return SearchCell(snapshot, table);

            default:
                var linkedAll = snapshot.IsLinked(table.LogicalName, excludedFolders);
                var text = string.Join(" ",
                    table.IsAuditEnabled == true ? "A" : "·",
                    table.ChangeTrackingEnabled == true ? "C" : "·",
                    linkedAll switch { true => "L", false => "·", null => "?" },
                    table.SearchEnabled == true ? "S" : "·");
                return new SettingCell(SettingCellState.Text, text,
                    "A audit · C change tracking · L Synapse Link · S Dataverse search - a dot where it is off");
        }
    }

    private static string? LinkToolTip(TableSettingsSnapshot snapshot, string table)
    {
        if (snapshot.Synapse is null) return null;

        var profiles = snapshot.Synapse.Tables
            .Where(t => string.Equals(t.Table, table, StringComparison.OrdinalIgnoreCase))
            .Select(t => snapshot.Synapse.Profiles.FirstOrDefault(p => p.Id == t.ProfileId))
            .OfType<SynapseLinkProfile>()
            .Select(p => $"{p.Name} → {p.FolderLabel} ({p.State ?? "unknown"})")
            .ToList();

        return profiles.Count == 0 ? "In no link profile" : string.Join("\n", profiles);
    }

    /// <summary>
    /// Search as configured (the table's flag) checked against search as it is (the index):
    /// enabled but not yet in the index, or stuck part-way through syncing, is a problem.
    /// </summary>
    private static SettingCell SearchCell(TableSettingsSnapshot snapshot, TableSetting table)
    {
        var indexed = snapshot.Search?.Tables.FirstOrDefault(t => string.Equals(t.Table, table.LogicalName, StringComparison.OrdinalIgnoreCase));

        if (table.SearchEnabled != true)
        {
            return indexed is null
                ? SettingCell.Flag(table.SearchEnabled)
                : new SettingCell(SettingCellState.Problem, "Off", "Off for the table, but still in the index");
        }

        if (snapshot.Search is null) return SettingCell.Flag(true, "The search index could not be read");
        if (!snapshot.Search.IsProvisioned) return new SettingCell(SettingCellState.Inactive, "On", $"Dataverse search is {snapshot.Search.StatusLabel.ToLowerInvariant()}");
        if (indexed is null) return new SettingCell(SettingCellState.Problem, "On", "On for the table, but not in the index yet");

        var tip = $"{indexed.StatusLabel} · {indexed.IndexedColumns.Count} columns indexed" +
                  (indexed.LastDataSync is { } last ? $" · last synced {last:g}" : string.Empty);

        return indexed.IsComplete
            ? new SettingCell(SettingCellState.On, "On", tip)
            : new SettingCell(SettingCellState.Problem, "On", $"Search index sync is {indexed.StatusLabel.ToLowerInvariant()}\n{tip}");
    }

    /// <summary>
    /// Column-level audit across environments, for the tables audited in at least one - a column's
    /// flag does nothing while its table is not audited, so including the rest would bury the
    /// real findings. Where the table is not audited in an environment, its cell says so.
    /// </summary>
    public static IReadOnlyList<SettingRow> Columns(IReadOnlyList<TableSettingsSnapshot> snapshots, IEnumerable<string> scope)
    {
        var environments = snapshots.Select(s => s.Environment).ToList();
        var rows = new List<SettingRow>();

        foreach (var table in scope.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            var byEnvironment = snapshots
                .Select(s => s.Columns.TryGetValue(table, out var columns)
                    ? columns.ToDictionary(c => c.LogicalName, StringComparer.OrdinalIgnoreCase)
                    : null)
                .ToList();

            var names = byEnvironment.Where(d => d is not null).SelectMany(d => d!.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase);

            foreach (var column in names)
            {
                var cells = snapshots.Select((s, i) =>
                {
                    if (!s.TablesRead) return SettingCell.Unknown("The tables could not be read here");
                    if (!s.Tables.TryGetValue(table, out var setting)) return SettingCell.Absent("table");
                    if (byEnvironment[i] is not { } columns) return SettingCell.Unknown("Columns were not read here");
                    if (!columns.TryGetValue(column, out var audit)) return SettingCell.Absent("column");

                    if (setting.IsAuditEnabled != false && s.Settings?.AuditEnabled != false) return SettingCell.Flag(audit.IsAuditEnabled);

                    var why = setting.IsAuditEnabled == false ? "The table is not audited here, so this has no effect" : "Auditing is off for the environment";
                    return new SettingCell(SettingCellState.Inactive, audit.IsAuditEnabled == true ? "On" : "Off", why);
                }).ToList();

                var label = byEnvironment.Select(d => d?.GetValueOrDefault(column)?.DisplayName)
                    .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));

                rows.Add(new SettingRow($"{table}|{column}", column, label is null ? table : $"{label} · {table}", environments, cells));
            }
        }

        return rows;
    }

    /// <summary>
    /// Every column not audited on a table that is, environment by environment - what the
    /// AuditSetupCheck gap report lists. Managed columns are included: an unaudited column is a
    /// gap whoever owns it.
    /// </summary>
    public static IReadOnlyList<AuditGap> Gaps(IReadOnlyList<TableSettingsSnapshot> snapshots, IEnumerable<string> scope)
    {
        var tables = scope.ToHashSet(StringComparer.OrdinalIgnoreCase);

        return snapshots
            .SelectMany(s => s.Columns
                .Where(c => tables.Contains(c.Key) && s.Tables.GetValueOrDefault(c.Key)?.IsAuditEnabled == true)
                .SelectMany(c => c.Value
                    .Where(column => column.IsAuditEnabled == false)
                    .Select(column => new AuditGap(s.Environment, c.Key, s.Tables[c.Key].DisplayName,
                        column.LogicalName, column.DisplayName, column.IsManaged))))
            .OrderBy(g => g.Table, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.Column, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => snapshots.Select(s => s.Environment).ToList().IndexOf(g.Environment))
            .ToList();
    }

    /// <summary>
    /// The gaps as a pivot: a row per column that is not audited somewhere, grouped by table, with
    /// a cell per environment - "Not audited", "Audited", or "—" where the table is not audited or
    /// the column is not there.
    /// </summary>
    public static IReadOnlyList<GapRow> GapMatrix(IReadOnlyList<TableSettingsSnapshot> snapshots, IReadOnlyList<AuditGap> gaps)
    {
        var rows = new List<GapRow>();

        foreach (var table in gaps.GroupBy(g => g.Table, StringComparer.OrdinalIgnoreCase))
        {
            var columns = table.GroupBy(g => g.Column, StringComparer.OrdinalIgnoreCase).ToList();
            var group = new GapGroup
            {
                Table = table.Key,
                Label = table.Select(g => g.TableLabel).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l)) ?? table.Key,
                ColumnCount = columns.Count,
                Environments = snapshots.Select(s => s.Environment)
                    .Where(e => table.Any(g => g.Environment == e)).ToList()
            };

            foreach (var column in columns)
            {
                var cells = snapshots.Select(s =>
                {
                    if (column.Any(g => g.Environment == s.Environment))
                        return new SettingCell(SettingCellState.Problem, "Not audited", "Changes to this column are not recorded here");
                    if (s.Tables.GetValueOrDefault(table.Key)?.IsAuditEnabled != true)
                        return new SettingCell(SettingCellState.Absent, "—", "The table is not audited here");
                    var audit = s.Columns.GetValueOrDefault(table.Key)?
                        .FirstOrDefault(c => string.Equals(c.LogicalName, column.Key, StringComparison.OrdinalIgnoreCase));
                    return audit is null
                        ? SettingCell.Absent("column")
                        : new SettingCell(SettingCellState.Off, "Audited");
                }).ToList();

                var first = column.First();
                rows.Add(new GapRow(group, first.Column, first.ColumnLabel, first.IsManaged, cells));
            }
        }

        return rows;
    }

    /// <summary>Every table in every link profile, environment by environment, profile by profile.</summary>
    public static IReadOnlyList<LinkRow> Links(IReadOnlyList<TableSettingsSnapshot> snapshots, ISet<string> excludedFolders)
    {
        var rows = snapshots
            .Where(s => s.Synapse is not null)
            .SelectMany(s => s.Synapse!.Tables.Select(t => (Snapshot: s, Table: t,
                Profile: s.Synapse.Profiles.FirstOrDefault(p => p.Id == t.ProfileId))))
            .Where(x => x.Profile is not null)
            .Select(x => new LinkRow(x.Snapshot.Environment, x.Profile!, x.Table,
                x.Profile!.IsActive && !excludedFolders.Contains(x.Profile.FolderLabel))
            {
                TableLabel = x.Snapshot.Tables.GetValueOrDefault(x.Table.Table)?.Label ?? x.Table.Table
            })
            .ToList();

        // One group per environment and profile; groups with problems first, and problems first in each.
        return rows
            .GroupBy(r => (r.Environment, r.Profile.Id))
            .Select(g =>
            {
                var group = new LinkGroup
                {
                    Environment = g.Key.Environment,
                    Profile = g.First().Profile,
                    IsCounted = g.First().IsCounted,
                    TableCount = g.Count(),
                    ProblemCount = g.Count(r => r.HasProblem)
                };
                return (Group: group, Rows: g.Select(r => r with { Group = group })
                    .OrderByDescending(r => r.HasProblem)
                    .ThenBy(r => r.TableLabel, StringComparer.CurrentCultureIgnoreCase)
                    .ToList());
            })
            .OrderByDescending(g => g.Group.ProblemCount > 0)
            .ThenByDescending(g => g.Group.IsCounted)
            .SelectMany(g => g.Rows)
            .ToList();
    }

    /// <summary>
    /// Each environment's search index against its configuration: every table either switched on
    /// for search or found in the index. Environments whose index is not provisioned contribute
    /// nothing - the environment view says why.
    /// </summary>
    public static IReadOnlyList<SearchIndexRow> SearchIndex(IReadOnlyList<TableSettingsSnapshot> snapshots) =>
        snapshots
            .Where(s => s.Search is { IsProvisioned: true })
            .SelectMany(s =>
            {
                var indexed = s.Search!.Tables.ToDictionary(t => t.Table, StringComparer.OrdinalIgnoreCase);
                var enabled = s.Tables.Values.Where(t => t.SearchEnabled == true).Select(t => t.LogicalName);

                return indexed.Keys.Union(enabled, StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .Select(table => new SearchIndexRow(
                        s.Environment,
                        table,
                        s.Tables.GetValueOrDefault(table)?.DisplayName,
                        s.Tables.GetValueOrDefault(table)?.SearchEnabled,
                        indexed.GetValueOrDefault(table)));
            })
            .ToList();

    /// <summary>The folders links write to, across every environment - what the matrix can be told to ignore.</summary>
    public static IReadOnlyList<string> Folders(IReadOnlyList<TableSettingsSnapshot> snapshots) =>
        snapshots.Where(s => s.Synapse is not null)
            .SelectMany(s => s.Synapse!.Profiles.Select(p => p.FolderLabel))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// The environment-wide settings side by side: whether auditing is on at all, retention, and
    /// the state of Dataverse search and the links.
    /// </summary>
    public static IReadOnlyList<SettingRow> Environments(IReadOnlyList<TableSettingsSnapshot> snapshots)
    {
        var environments = snapshots.Select(s => s.Environment).ToList();

        SettingRow Row(string name, string? detail, Func<TableSettingsSnapshot, SettingCell> cell) =>
            new(name, name, detail, environments, snapshots.Select(cell).ToList());

        static SettingCell FromSettings(TableSettingsSnapshot s, Func<EnvironmentDataSettings, SettingCell> cell) =>
            s.Settings is { } settings ? cell(settings) : SettingCell.Unknown("The organization settings could not be read");

        static SettingCell FromSearch(TableSettingsSnapshot s, Func<SearchIndexStatus, SettingCell> cell) =>
            s.Search is { } search ? cell(search) : SettingCell.Unknown("The search status could not be read");

        static SettingCell FromLinks(TableSettingsSnapshot s, Func<SynapseLinkSetup, SettingCell> cell) =>
            s.Synapse is { } links ? cell(links) : SettingCell.Unknown("Synapse Link could not be read");

        static SettingCellState IndexState(SearchIndexStatus search) => search switch
        {
            { IsProvisioned: true } => SettingCellState.On,
            { Status: "provisioninginprogress" } => SettingCellState.Problem,
            _ => SettingCellState.Off
        };

        static SettingCell Count(long? value) =>
            value is { } n ? new SettingCell(SettingCellState.Text, n.ToString("N0")) : SettingCell.Unknown("Not reported");

        return
        [
            Row("Auditing", "Start auditing - nothing is audited while this is off", s => FromSettings(s, x => SettingCell.Flag(x.AuditEnabled))),
            Row("Log access", "User access audit - sign-ins", s => FromSettings(s, x => SettingCell.Flag(x.UserAccessAuditEnabled))),
            Row("Read logs", "Read audit (Microsoft Purview)", s => FromSettings(s, x => SettingCell.Flag(x.ReadAuditEnabled))),
            Row("Retain audit logs", "Audit retention period", s => FromSettings(s, x => x.AuditRetentionDays is null
                ? SettingCell.Unknown("Not reported")
                : new SettingCell(SettingCellState.Text, x.RetentionLabel))),
            Row("Audited tables", "Tables with audit on", s => new SettingCell(SettingCellState.Text,
                s.Tables.Values.Count(t => t.IsAuditEnabled == true).ToString("N0"))),
            Row("Change-tracked tables", "Tables with change tracking on", s => new SettingCell(SettingCellState.Text,
                s.Tables.Values.Count(t => t.ChangeTrackingEnabled == true).ToString("N0"))),
            Row("Dataverse search", "The environment's search switch", s => FromSettings(s, x => SettingCell.Flag(x.SearchEnabled))),
            Row("Search index", "Provisioning state of the index", s => FromSearch(s, x => new SettingCell(IndexState(x), x.StatusLabel))),
            Row("Searchable tables", "Tables with search on", s => new SettingCell(SettingCellState.Text,
                s.Tables.Values.Count(t => t.SearchEnabled == true).ToString("N0"))),
            Row("Indexed tables", "Tables in the index / of them fully synced", s => FromSearch(s, x => new SettingCell(
                x.Tables.All(t => t.IsComplete) ? SettingCellState.Text : SettingCellState.Problem,
                $"{x.Tables.Count:N0} / {x.Tables.Count(t => t.IsComplete):N0}",
                x.Tables.Any(t => !t.IsComplete)
                    ? "Not fully synced: " + string.Join(", ", x.Tables.Where(t => !t.IsComplete).Select(t => t.Table))
                    : null))),
            Row("Search documents", "Rows in the index", s => FromSearch(s, x => Count(x.DocumentCount))),
            Row("Search storage (MB)", "Size of the index", s => FromSearch(s, x => Count(x.StorageMegabytes))),
            Row("Link profiles", "Synapse and Fabric link profiles / of them active", s => FromLinks(s, x => new SettingCell(
                SettingCellState.Text, $"{x.Profiles.Count:N0} / {x.Profiles.Count(p => p.IsActive):N0}",
                x.Profiles.Count == 0 ? null : string.Join("\n", x.Profiles.Select(p => $"{p.Name} → {p.FolderLabel} ({p.State ?? "unknown"})"))))),
            Row("Linked tables", "Tables exported by an active profile, and of them not keeping up", s => FromLinks(s, x =>
            {
                var active = x.Profiles.Where(p => p.IsActive).Select(p => p.Id).ToHashSet();
                var tables = x.Tables.Where(t => t.IsActive && active.Contains(t.ProfileId)).ToList();
                var behind = tables.Count(t => !t.IsHealthy);
                return new SettingCell(behind > 0 ? SettingCellState.Problem : SettingCellState.Text,
                    behind > 0 ? $"{tables.Count:N0} ({behind:N0} behind)" : tables.Count.ToString("N0"));
            }))
        ];
    }
}
