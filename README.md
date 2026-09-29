# Power Platform Object Search

A Windows desktop app (WPF, .NET 10) for keyword-searching the objects in a Power Platform /
Dataverse solution. Connects with interactive OAuth, defaults to the environment's **default
solution**, lists every object with a filterable **Object type** column, and links each name
straight into the maker portal.

## Features

- **OAuth sign-in** (authorization code + PKCE) through the system browser, so existing SSO and
  MFA sessions are reused. Tokens are cached encrypted with DPAPI, so restarts reconnect silently.
- **Environment sidebar** — open as many environments as you like; each is an entry in the sidebar,
  restored on the next launch in the order you left them (`Ctrl+T` new, `Ctrl+W` or the entry's ✕
  to close; drag an entry or use `Ctrl+Shift+PgUp` / `PgDn` to reorder).
- **Environment type at a glance** — every environment carries a colour and a badge for its type
  (production, default, sandbox, developer, trial), read from the Power Platform API. An
  environment whose type cannot be read shows as *Unknown*.
- **Light and dark themes** — follows the Windows app theme by default; the sun icon at the bottom
  of the sidebar picks Light or Dark instead.
- **Cross-tenant** — each tab discovers its environment's tenant from the Dataverse 401 challenge
  and holds its own account, so tabs in different tenants work simultaneously. *Switch account*,
  in the account menu at the top right, re-signs a single tab without touching the others.
- **Solution picker** — defaults to the default solution; any visible solution can be selected.
- **Instant keyword search** — space-separated terms, all of which must match; matched against
  name, display name, schema name, object type, related table, owner and object id.
- **Filters** — type, sub type, managed state and layer, each listing what is present with a count.
  A filter in use is highlighted, and **Clear** resets them all.
- **Name as a maker portal link** — click to open the object in <https://make.powerapps.com>.
- **Detail pane** — the selected object's related table, owner, id and layer state, with buttons to
  open it, see its solutions and dependencies, or copy its name, link or id. Right-click a row for
  the same copy actions; the pane can be hidden from the toolbar.
- **Object details** (*Details…*) — solutions, layers and dependencies for any object, plus, by type:
  - **Cloud flows**: recent run history from Dataverse, with each run's error and a link to the run
    in Power Automate, and the flow's JSON definition to copy or save.
  - **Classic workflows**: recent system jobs and their errors.
  - **Plug-in assemblies, types and steps**: the plug-in trace log, with trace text and exceptions
    (and a warning when tracing is switched off).
  - **Web resources**: the decoded source of scripts, HTML, CSS, XML, SVG and RESX files.
  - **Environment variables**: the current value in this environment, the default, and which applies.
  - **Tables**: their columns, relationships, keys, forms and views, and an exact **Count rows**
    that finds the last page of 5,000 instead of reading every row.
- **Solution history** — every import, upgrade, uninstall and export in an environment, with the
  error for any that failed.
- **Reference data comparison** — diff the *rows* of chosen tables between two environments, keyed
  on the primary key, an alternate key or columns you pick. Saved as named configurations.
- **Reconcile differences** — write selected rows from the source into the target. Production
  environments are refused unless explicitly allowlisted, and deleting needs its own confirmation.

## Build and run

```powershell
dotnet build
dotnet run
```

Produces `bin\Debug\net10.0-windows\PPObjectSearch.exe`. To publish a single self-contained exe
that runs without .NET installed:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```

## Releases

`.github/workflows/release.yml` builds that same single-file exe on `windows-latest`. Push a tag
to cut a release:

```powershell
git tag v1.0.0
git push origin v1.0.0
```

The exe is attached to the GitHub release, with notes generated from the commits. Running the
workflow manually from the Actions tab instead just uploads the exe as a build artifact.

## Usage

1. Paste the environment URL (e.g. `https://contoso.crm11.dynamics.com`) and press **Connect**.
2. Sign in in the browser window that opens.
3. The default solution loads automatically. Type keywords in **Search** and/or pick an
   **Object type** to narrow the list.
4. Click an object's name to open it in the maker portal.

## How objects are read

The object list comes from the `msdyn_solutioncomponentsummary` virtual table — the same source
the maker portal's own solution object list uses — so display names, schema names and component
type labels all arrive in a single paged query. The whole solution is loaded once, then searching
and filtering happen in memory, which keeps typing instant even on the default solution of a large
environment.

## Comparing reference data

**Compare data** (`Ctrl+Shift+D`) diffs the rows of chosen tables between two connected
environments — currencies, categories, configuration tables, anything whose *contents* are part of
the solution rather than just its shape. It is read-only: nothing is ever written to either
environment.

1. Pick a **source** and a **target** from the connected tabs.
2. **+ Add** lists every table in the source environment. Tick the ones to check.
3. **Settings…** on a table (or double-click it) decides how it is compared:
   - **What identifies a row** — the primary key, one of the table's alternate keys, or columns you
     pick. Primary keys only agree between environments where the rows were *deployed*; rows built
     separately in each environment carry different ids, so an alternate key or a natural key
     column is usually the right choice.
   - **Row filter** — an optional OData `$filter` (e.g. `statecode eq 0`), applied to both sides.
   - **Columns to compare** — every readable column, minus the primary id and the
     created/modified/owner housekeeping columns, which differ for every deployed row and would
     bury the real differences. **Defaults** puts that back.
4. **Compare**. Each row lands as *only in source*, *only in target*, *values differ* or *match*,
   and selecting one shows its differing columns side by side (untick **Differences only** to see
   every compared column). The filter above the grid switches between the statuses, and each
   table in the list shows how many of its rows differ.

**Export CSV** writes one line per differing column — table, key, name, status, column, and each
environment's value — so the output can be sorted and filtered outside the app.

Some details worth knowing:

- **Lookups are matched on their label, not their id**, because the id behind a lookup only agrees
  where the target row was deployed. Untick **Match lookups by name** to compare the raw ids
  instead; the result is re-judged from rows already read, without asking either environment again.
- **Columns are intersected across both environments.** A column that exists only in the source
  would make the target's query fail outright, so it is left out and reported in the warnings.
- **Values are normalised per column type** before comparing, so `1.0000` against `1.0`, or the
  same instant written with a different UTC offset, is not reported as a difference. Text is
  compared exactly apart from surrounding whitespace, so a change of case *is* a difference.
- **Anything that makes the result partial is said out loud** in the warnings strip: a table
  missing from one side, a non-unique key, a column that exists on one side only, or a table that
  hit the row cap. A truncated table reports every unread row as missing, so the cap matters —
  raise it or add a filter.

Configurations are saved by name into `settings.json` (below) and picked from the dropdown, so a
data set worth checking regularly is set up once. An amber dot on **Save** means there are unsaved
changes; **⋯** beside it holds New, Rename and Delete.

## Reconciling differences

Tick rows in the results grid — or select them, one or a range with `Shift`/`Ctrl` — and
**Reconcile N rows…** writes them from the source into the target. This is the only part of the
app that changes anything.

Three actions, each a card you switch on or off (rows for an action that is off stay listed,
faded, and are never written):

| Row status | Action | On by default |
|---|---|---|
| Only in source | **Create** it in the target, reusing the source's id | Yes |
| Values differ | **Update** the target row | Yes |
| Only in target | **Delete** it from the target | **No** |

Nothing is written until **Apply**. Deleting is off to begin with, and turning it on adds a
separate acknowledgement naming how many rows will go — Dataverse deletes cannot be undone.

- **Only the columns you compared are written.** A column excluded from the comparison cannot be
  changed by reconciling, and an update writes only the columns that actually differ.
- **Created rows keep the source's id**, so the two environments converge on one id and later
  comparisons match on the primary key. A create is a POST, never an upsert, so an id already in
  use fails that row rather than overwriting whatever holds it.
- **Lookups are resolved by name** against the target: the label the source showed is looked up in
  the table the lookup actually points at. If nothing matches, or several rows do, that record is
  abandoned with the reason — a reference row written with a missing or guessed reference is worse
  than one not written.
- **Read-only columns are left out**, not failed on. Calculated and rollup columns read like any
  other but Dataverse refuses them on write, so they are skipped and named in the result.
- Rows are written one at a time and each reports its own result, so a failure part-way through
  leaves the successful rows written and says exactly which ones did not go. Stopping a run does
  not roll anything back.

After a run the comparison is re-read automatically, so the grid shows what is now true rather
than what was true before the writes.

### The production guard

**Writing to a production environment is refused unless that environment is named in
`AllowProductionWrites`.** The environment type comes from the Power Platform API — Dataverse
itself does not carry its own SKU — and the guard fails closed:

- **Sandbox, developer and trial** environments are writable with no configuration.
- **Production** is refused unless allowlisted.
- **The tenant's default environment** is refused on the same terms. It is not labelled
  production, but everyone in the tenant is in it.
- **An environment whose type could not be read** is refused on the same terms too. Not knowing is
  not the same as knowing it is safe, so an unreachable API, a missing consent or an unrecognised
  SKU all land on "blocked".

Each entry clears exactly the one environment it names — matched on host, so the scheme and a
trailing slash do not matter, but there are no wildcards and no suffix matching. The app never
writes to this list; you add to it by hand.

```jsonc
"AllowProductionWrites": [
  "https://contoso.crm11.dynamics.com"
]
```

The reconcile window always states which environment it is writing to, what type it is, and
whether the guard cleared it — in green when it did, in red when it did not, with **Apply**
unavailable.

The type check needs a Power Platform API token for the signed-in account. It is only ever
requested silently, so this never opens a sign-in window on its own; where no token is to be had,
the environment simply reads as unknown and is guarded.

## Authentication

By default the app uses Microsoft's pre-consented public client for Dataverse tooling
(`51f81489-12ee-4a9e-aaae-a2591f45987d`, the one PAC CLI and XrmToolBox use), so **no app
registration is needed**. If your tenant blocks it, register your own public client with the
`http://localhost` redirect URI and the Dynamics CRM `user_impersonation` delegated permission,
then set `ClientId` in settings (below).

## Settings

`%LOCALAPPDATA%\PPObjectSearch\settings.json` — written automatically, all fields optional.

```jsonc
{
  // Restored tabs, maintained by the app.
  "Tabs": [
    {
      "EnvironmentUrl": "https://contoso.crm11.dynamics.com",
      "TenantId": "00000000-0000-0000-0000-000000000000",
      "AccountId": "<msal home account id>",
      "SolutionUniqueName": "Default"
    }
  ],

  // Use your own app registration instead of the built-in public client.
  "ClientId": null,

  // Solution to select on connect when a tab has no remembered one.
  "DefaultSolutionUniqueName": "Default",

  // "System" follows the Windows app theme; "Light" or "Dark" fixes it. Set from the sidebar.
  "Theme": "System",

  // Whether the main window's object detail pane is showing.
  "IsDetailPaneOpen": true,

  // Power Platform environment ids for maker portal links, keyed by host. Only needed if
  // automatic discovery is blocked in your tenant.
  "EnvironmentIds": {
    "contoso.crm11.dynamics.com": "00000000-0000-0000-0000-000000000000"
  },

  // Override the maker portal URL per component type (by type number or component logical name).
  // Placeholders: {envId} {envUrl} {solutionId} {objectId} {entitySet} {name} {logicalName}
  //               {primaryEntity} {primaryEntityId} {workflowIdUnique} {componentType}
  "MakerLinkTemplates": {
    "1": "https://make.powerapps.com/environments/{envId}/entities/{objectId}",

    // Cloud flows are addressed by workflowid under a "cloudflows" segment. If a tenant wants
    // the solution-independent id instead, swap {objectId} for {workflowIdUnique}:
    "29": "https://make.powerapps.com/environments/{envId}/solutions/{solutionId}/objects/cloudflows/{objectId}/view"
  },

  // Environments this app may write reference data to despite being production - or despite their
  // type being unreadable, which is guarded the same way. Sandbox, developer and trial
  // environments need no entry. Matched on host; no wildcards. Never written by the app.
  "AllowProductionWrites": [
    "https://contoso.crm11.dynamics.com"
  ],

  // Saved reference data comparisons, maintained by the Compare data window. Editable by hand -
  // this is the shape a configuration takes.
  "ReferenceDataConfigurations": [
    {
      "Name": "Core reference data",
      "MatchLookupsByName": true,
      "MaxRowsPerEntity": 5000,
      "Entities": [
        {
          "LogicalName": "contoso_category",
          "DisplayName": "Category",

          // "PrimaryId", "AlternateKey" or "Columns".
          "KeySource": "AlternateKey",
          "AlternateKeyName": "contoso_categorycodekey",

          // The key's columns, kept so the comparison still works if the key is later dropped.
          // Also the key itself when KeySource is "Columns".
          "KeyColumns": ["contoso_code"],

          // OData $filter applied to both environments. Omit for every row.
          "Filter": "statecode eq 0",

          // Columns left out of the value comparison. Omit the property entirely (not an empty
          // list) to take the defaults: the primary id and the created/modified/owner columns.
          "ExcludedColumns": ["contoso_categoryid", "createdon", "modifiedon"],

          "IsEnabled": true
        }
      ]
    }
  ]
}
```

The token cache lives next to it in `msal.cache`, encrypted with DPAPI for the current Windows
user. **Sign out all** deletes it and forgets every account.

## Maker portal links

The solution explorer addresses an object as
`/solutions/{solutionId}/objects/{entitySetName}/{objectId}/view`, where the segment is the
component type's OData entity set name — `roleeditorlayout` becomes `/objects/roleeditorlayouts`.
That segment is read from table metadata rather than guessed, so it is correct for component
types this app has never heard of. Tables and columns use the table designer instead, and cloud
flows are the known exception to the rule: they are `workflow` rows but the portal files them
under `cloudflows`.

Links degrade in steps rather than collapsing to nothing. A component whose object route cannot
be built still links to **that type's object list inside the solution**; only a component whose
type cannot be resolved at all falls back to the solution page. Nothing renders a link that is
known to be dead.

Maker portal routes are not a documented, versioned contract, so anything wrong for your tenant
can be corrected via `MakerLinkTemplates` without a rebuild.

Links need the Power Platform environment id, which is resolved from the organization metadata
and, failing that, from the Global Discovery Service. If neither is reachable the status bar says
so and the names render as plain text; set the id under `EnvironmentIds` to restore linking.
