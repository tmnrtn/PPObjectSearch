# Power Platform Object Search

A Windows desktop app (WPF, .NET 10) for keyword-searching the objects in a Power Platform /
Dataverse solution. Connects with interactive OAuth, defaults to the environment's **default
solution**, lists every object with a filterable **Object type** column, and links each name
straight into the maker portal.

## Features

- **OAuth sign-in** (authorization code + PKCE) through the system browser, so existing SSO and
  MFA sessions are reused. Tokens are cached encrypted with DPAPI, so restarts reconnect silently.
- **Environment tabs** — open as many environments as you like side by side. Tabs are restored on
  the next launch (`Ctrl+T` new tab, `Ctrl+W` close).
- **Cross-tenant** — each tab discovers its environment's tenant from the Dataverse 401 challenge
  and holds its own account, so tabs in different tenants work simultaneously. *Switch account*
  re-signs a single tab without touching the others.
- **Solution picker** — defaults to the default solution; any visible solution can be selected.
- **Instant keyword search** — space-separated terms, all of which must match; matched against
  name, display name, schema name, object type, related table, owner and object id.
- **Filterable object type column** — the dropdown lists every type present with a count.
- **Name as a maker portal link** — click to open the object in <https://make.powerapps.com>.
  Right-click a row to copy the name, the link, or the object id.
- **Reference data comparison** — diff the *rows* of chosen tables between two environments, keyed
  on the primary key, an alternate key or columns you pick. Saved as named configurations.

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
2. **Add tables…** lists every table in the source environment. Tick the ones to check.
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
   and selecting one shows every compared column side by side with the differences highlighted.

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
data set worth checking regularly is set up once.

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
