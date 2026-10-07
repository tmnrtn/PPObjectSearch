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
- **Browser profile per environment** — right-click an environment and choose **Open links in**
  to pick an Edge or Chrome profile (or the default browser). Maker portal and Power Automate
  links for that environment open there, and so do its sign-in prompts - so each tenant signs in
  in the profile already signed in to it. Saved per environment; a profile that has gone away
  falls back to the default browser.
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
- **Favourites, saved searches and recent objects** — star objects (right-click, or the star
  column) and show only them with **Favourites**; **Saved** keeps a search's words and filters under
  a name; **Recent** reopens the details of objects you looked at lately. All per environment, in
  `%LOCALAPPDATA%\PPObjectSearch\library.json`.
- **Name as a maker portal link** — click to open the object in <https://make.powerapps.com>.
- **Detail pane** — the selected object's related table, owner, id and layer state, with buttons to
  open it, see its solutions and dependencies, or copy its name, link or id. **Open in details**
  goes straight to a tab of the details window - Design, Run history or Definition for a flow;
  Columns, Relationships, Forms or Views for a table. A cloud flow shows its last run, and how
  many of its last 20 failed; an environment variable shows its current value. Right-click a row
  for the same copy actions; the pane can be hidden from the toolbar. Select several rows
  (Shift/Ctrl+click) and the menu copies all their names, links or ids - one per line - or copies
  them as a table for Excel, opens details for each, checks their layers or exports just them.
- **Object details** (*Details…*, or double-click a row) — solutions, layers and dependencies for
  any object, with the type's own tabs first. Each window names the environment it came from. Open
  as many as you like: each opens a step down and right from the last, and **Close all details**
  (`Ctrl+Shift+W`) in the sidebar closes them together. By type, details also show:
  - **Cloud flows**: whether the flow is on, a link to it in Power Automate, recent run history
    (Dataverse's, plus runs still in progress or just finished from Power Automate), with each
    run's error and a link to the run, and the flow's JSON definition to copy or save. **Design** draws the flow
    as the designer does, read-only: trigger, steps in run-after order, parallel branches side by
    side, and conditions, switches, loops and scopes that collapse. Where a step runs after
    something other than plain success, its connector is dashed red and shows the outcomes it
    waits on (Succeeded, Failed, Timed out, Skipped); search finds a step by name, type,
    connector or condition, and selecting one shows its JSON. A "Run a child flow" step names
    the flow it calls. **Copy as Mermaid** copies the flow as a Mermaid flowchart for a wiki,
    README or pull request. **Diagram** on a run in Run history
    draws that run over the design, read step by step from the Power Automate API: each step's
    outcome and duration, skipped and unreached steps faded, loops with their iteration count,
    and the first failure selected with its error. The run sits in the diagram's toolbar, with
    **First failure** and a button to clear it. A step inside a loop lists its iterations; a
    step's **Inputs** and **Outputs** are fetched only when asked for, and never saved. Run details are
    kept for about 28 days, and need you to own or co-own the flow (or be an environment admin).
  - **Classic workflows**: recent system jobs and their errors.
  - **Plug-in assemblies, types and steps**: the plug-in trace log, with trace text and exceptions
    (and a warning when tracing is switched off).
  - **Web resources**: the decoded source of scripts, HTML, CSS, XML, SVG and RESX files, with
    syntax highlighting in both themes, line numbers and Ctrl+F search. Very large or minified
    files are shown as plain text, since highlighting them would make the viewer crawl.
  - **Environment variables**: the current value in this environment, the default, and which applies.
  - **Tables**: their columns, relationships, keys, forms and views; Dataverse's stored row count
    (refreshed about daily) as soon as the window opens, and an exact, current **Count rows** -
    one aggregate request up to 50,000 rows, a search for the last page of 5,000 beyond that.
- **Solution history** (in the sidebar, under the selected environment) — every import, upgrade,
  uninstall and export in an environment, with the error for any that failed. **Import log…** on
  a row opens that import's log (`importjob.data`) as a grid: each component's type, result
  (success, warning, failure), error and timing - filtered to failures and warnings when there are
  any. Double-click a component in the loaded list to open it; **Copy failures as Markdown** and
  **Save raw XML…** are on the toolbar. An import still running shows its progress, refreshed every
  five seconds, until its log is ready.
- **Reference data comparison** — diff the *rows* of chosen tables between two environments, keyed
  on the primary key, an alternate key or columns you pick. Saved as named configurations.
- **Reconcile differences** — write selected rows from the source into the target. Production
  environments are refused unless explicitly allowlisted, and deleting needs its own confirmation.
- **Environment admin** (in the sidebar, under the selected environment) — read-only views of
  users, security roles, mailboxes and queues. See [Environment admin](#environment-admin).
- **Admin tools** (in the sidebar, under the selected environment) — sync an Entra group team with its Entra (RBAC)
  group, and make a queue's members match a team's. Every change is previewed and confirmed first,
  under the same production guard.

## Build and run

```powershell
dotnet build
dotnet run
```

Produces `bin\Debug\net10.0-windows\PPObjectSearch.exe`. To publish a single self-contained exe
that runs without .NET installed:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:NuGetLockFilePath=obj/publish.packages.lock.json -o publish
```

## Tests

```powershell
dotnet test tests/PPObjectSearch.Tests
```

xUnit tests for everything that can run without a live tenant: diffing, comparison and
reconcile planning, maker portal links, the environment-type probe and the production guard,
the membership planners and the confirmation window's gating, and the Dataverse and Graph
clients' requests. The HTTP clients are exercised against an in-memory fake
(`tests/PPObjectSearch.Tests/Infrastructure`), so no test touches the network. The XAML is checked
too: every resource a view uses must be defined, and the light and dark themes must define the
same keys. CI reports line and branch coverage on each run's summary page. The release
workflow runs them before publishing.

Each push to main is also analysed on the self-hosted SonarQube server
(`.github/workflows/sonarqube.yml`), with test results and coverage. Its address and token are
read from 1Password (`op://CI/SonarQube/url` and `op://CI/SonarQube/token`) through a service
account, whose token is the repository secret `OP_SERVICE_ACCOUNT_TOKEN`.

## Command line (ppos)

`ppos.exe`, released beside the app, runs the read-only checks a pipeline wants before promoting a
release. Nothing it does writes to an environment - reconciling is only ever offered in the window.

```text
ppos diff --solution <name> --left <url> --right <url> [--format md|csv|json] [--out <file>]
ppos compare-data --config <name> --source <url> --target <url> [--out <file.csv>]
ppos readiness --solution <name> --source <url> --target <url> [--out <file.md>] [--fail-on blocker|warning]
```

- **diff** - the solution's components missing from either environment (matched by id, then type
  and name).
- **compare-data** - a saved reference-data comparison from `settings.json`, differences as CSV.
- **readiness** - the [readiness check](#readiness-check) as Markdown.

Exit codes: `0` nothing found, `1` differences or blockers found, `2` usage error, `3` the run
failed - so a pipeline step fails exactly when there is something to look at. Progress and the
summary go to standard error; the report goes to standard output or `--out`.

To run unattended, sign in as an app registration: set `PPOS_TENANT_ID`, `PPOS_CLIENT_ID` and
`PPOS_CLIENT_SECRET` - or `PPOS_CLIENT_CERTIFICATE` (a `.pfx` path) with
`PPOS_CLIENT_CERTIFICATE_PASSWORD`. The app user needs read access in each environment. Without
them, `ppos` uses the account signed in to the app, opening a browser if it must.

## Releases

`.github/workflows/release.yml` builds that same single-file exe on `windows-latest`. Push a tag
to cut a release:

```powershell
git tag v1.0.0
git push origin v1.0.0
```

The exe is attached to the GitHub release with a `.sha256` checksum and a build provenance
attestation (`gh attestation verify PPObjectSearch.exe --repo tmnrtn/PPObjectSearch`), with notes
generated from the commits. Package versions are pinned and recorded in `packages.lock.json`;
CI restores in locked mode, and Dependabot proposes updates as pull requests. Running the
workflow manually from the Actions tab instead just uploads the exe as a build artifact.

## Usage

1. Paste the environment URL (e.g. `https://contoso.crm11.dynamics.com`) and press **Connect**.
2. Sign in in the browser window that opens.
3. The default solution loads automatically. Type keywords in **Search** and/or pick an
   **Object type** to narrow the list.
4. Click an object's name to open it in the maker portal.

**Ctrl+K** opens the command palette: type a few letters of any command - every sidebar, toolbar
and admin action, scoped to the current environment - a connected environment to switch to, a
recent object or saved search, or (from two letters) any object in the list to open its details.
Letters match in order, word starts first, so `cmpd` finds *Compare data*. Each command shows its
shortcut. ↑/↓ choose, Enter runs, Esc closes.

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
     bury the real differences. **Defaults** puts that back. Wherever a money column is compared
     the row's currency (`transactioncurrencyid`) is compared and written with it, even if it was
     excluded - 100 EUR is not 100 USD.
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
  compared and written exactly, whitespace included, so a change of case or a trailing
  space *is* a difference.
- **Anything that makes the result partial is said out loud** in the warnings strip: a table
  missing from one side, a non-unique key, a column that exists on one side only, or a table that
  hit the row cap. A truncated table reports every unread row as missing, so the cap matters —
  raise it or add a filter. Rows are read in id order, so both environments stop at the same
  place, and reconciling will not create or delete a row whose absence could just mean it was
  not read, nor write a row whose key is not unique.

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
  than one not written. Where a name is shared but only one of those rows is active, that one is
  used. With **Match lookups by name** unticked, a lookup is bound by its id instead, once that row
  is found in the target.
- **Rows are written parents first.** Creates and updates go table by table, a table before the
  tables that point at it (and, in a table that points at itself, a parent before its children),
  and a row written earlier in the run resolves the lookups of the rows after it. Deletes go
  last, children first.
- **Read-only columns are left out**, not failed on. Calculated and rollup columns read like any
  other but Dataverse refuses them on write, so they are skipped and named in the result.
- **A row changed since the comparison is not overwritten.** Updates and deletes carry the
  version of the target row that was compared (`If-Match`), so a row someone edited or removed in
  the meantime fails with a note to compare again instead of losing their change.
- Rows are written one at a time and each reports its own result, so a failure part-way through
  leaves the successful rows written and says exactly which ones did not go. Stopping a run does
  not roll anything back.

After a run the comparison is re-read automatically, so the grid shows what is now true rather
than what was true before the writes.

### Run log, plan export and undo

- **Every write is recorded** in a run log: one JSON line per write under
  `%LOCALAPPDATA%\PPObjectSearch\writes\`, one file per run, with the time, the signed-in account,
  the environment, the table, row id and action, the columns, what was sent, the outcome, and
  how to reverse it. **Open run log** shows the file. The membership windows record each add and
  remove the same way.
- **The target row is copied before every update or delete.** The copy goes in the run log; a
  row that cannot be read first is not written.
- **Undo run** reverses what the window wrote, newest first: deleted rows are re-created with
  their original id from every column the target accepts on a create (and set back to inactive
  where they were), updated columns are set back to what they held, and created rows are
  deleted. Rows Dataverse removed through cascading relationships are not restored, and edits
  made to the same columns since the run are overwritten.
- **What else a delete reaches is counted first.** Switching Delete on reads the table's
  relationships and counts the rows that point at the ones being deleted: those Dataverse would
  delete with them (cascade), those that would make the delete fail (restrict), and those that
  would lose their reference (remove link). The counts appear above the delete acknowledgement,
  which cannot be ticked until they are in.
- **Columns can be left out of one run.** The *Columns* list under the action cards names every
  column the run would write; unticking one leaves it out of this run only, without changing the
  saved configuration. An update with nothing left to write is skipped. Key columns are always
  written.
- **Export plan…** saves the planned changes as CSV - one line per changed column - so they can be
  reviewed or attached to a change ticket before anything is applied.

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
trailing slash do not matter, but there are no wildcards and no suffix matching.

To allow writes to an environment, right-click it in the sidebar and choose **Allow writes to this
environment…**. It is only offered where the guard applies, and it asks you to confirm, naming the
environment and its type; the answer defaults to No. An allowlisted environment carries an amber
unlock icon in the sidebar, and **Stop allowing writes** on the same menu takes it off the list
again. Open windows pick the change up at their next preview. The list can also be edited by hand
(close the app first — it rewrites `settings.json` from memory):

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

## Environment admin

**Users**, **Security roles**, **Mailboxes** and **Queues** in the sidebar, under the selected
environment, open one window with a tab for each. It only reads. Each tab reads the first time it
is opened, and links move between them: a user's role opens it on the Security roles tab, a
queue's mailbox on the Mailboxes tab, a mailbox's owner on the Users or Queues tab. **Back** and
**Forward** (Alt+Left / Alt+Right, or the mouse's back and forward buttons) retrace those links.

- **Users** — every user, searchable, filtered by user type (application user, or the access
  mode: Read-Write, Administrative, Non-interactive...), business unit and status (enabled users
  by default). A user shows their teams, and their security roles both direct and through each
  team, each in the business unit it is held in, their field security profiles (direct and through
teams - access to secured columns, which roles do not grant), and their mailbox's approval and
test result.
- **Security roles** — each role once, at the business unit it is defined in. A role shows its
  privileges as the role editor lays them out (a row per table, Create to Share, each with its
  reach: User, BU, Parent, Org), its other privileges (Export to Excel, Bulk delete...), and the
  users and teams holding it in any business unit.
- **Mailboxes** — filtered by owner (users, queues), approval and test result. A mailbox shows its
  approval (and the Exchange admin's), incoming, outgoing and appointment status and delivery
  method, whether each is enabled, when it was last tested, and its email server profile. The
  test result counts only the directions the mailbox delivers.
- **Queues** — public or private, active by default. A queue shows its owner, business unit, item
  count, email settings (which email it converts, unsolicited email, delivery, approval), its
  mailbox's approval and test result, and its members.

### Security lookup

**Security lookup** (sidebar, under the admin views) answers the questions the role editor does
not. Read-only, and each view exports to CSV:

- **Who can…** - pick a privilege (Create, Read, Write, Delete, Append, Append to, Assign, Share)
  and a table: every role that grants it and how far, then every user and team holding those
  roles, once each, with the widest depth and every path ("Sales Manager via team EMEA in Sales").
- **Effective access** - a user's privileges across every role they hold, directly and through
  their teams: the widest depth per table and action, with the roles that grant it on hover.
- **Secured column** - the field security profiles that grant read, create or update on a column,
  and the users and teams holding each.
- **Compare roles** - two roles side by side, or the same role in two connected environments
  ("dev has Organization delete, prod has Business unit"), listing only what differs.

## Admin tools

**Entra team sync…** and **Queue membership sync…**, marked *WRITES* in the sidebar under the
selected environment, are two membership tools. An environment allowed writes shows **Writes
allowed** in the header of every window opened from it. Both only read until you ask
for a preview of changes; nothing is written until you confirm it in a separate window that lists
every user affected, states which environment it is writing to, and applies the
[production guard](#the-production-guard). Removing anyone needs its own acknowledgement.

### Entra team sync

Pick a team linked to an Entra group (AAD security or Office group team). The team's members are
read from Dataverse and the group's from Microsoft Graph — *transitively*, because group teams
honour nested groups — and matched on Entra object id, falling back to UPN. Each person is
**Both**, **Team only** or **Group only**.

Dataverse syncs group teams lazily, when a user signs in, so some difference is normal: *group
only* is usually someone who has not used the environment since joining; *team only* is usually
someone removed from the group but not yet re-synced.

- **Sync from Entra…** previews Dataverse's own `SyncGroupMembersToTeam`: who it is expected to
  remove, and which group-only users it can add — only those who already have a Dataverse user
  record. Dataverse decides the actual changes, so after the run the team is read again and each
  row says whether its change landed. Unavailable when the group cannot be found in Entra, since a
  sync against a missing group could empty the team.
- **Diagnose** explains both kinds of difference. For each team-only user it asks Entra — by
  object id, then UPN, then whether Entra itself calls them a member — why they were left behind.
  For each group-only user it looks up their Dataverse user, by object id and then UPN, to say why
  the sync has not added them: disabled in Entra, excluded by the team's membership type, no
  Dataverse user, a user linked to a different Entra id, a disabled user, or one the sync should add.
- **Pull in group members…** provisions group-only users who have no Dataverse user, or a disabled
  one, by making a WhoAmI request as each of them (the `CallerObjectId` header). That triggers
  Dataverse's just-in-time user sync, the same as their own first sign-in. It needs the *Act on
  Behalf of Another User* privilege (Delegate role, or System Administrator). Afterwards
  `SyncGroupMembersToTeam` runs so they join the team — but only if the team has nobody the sync
  would remove; otherwise use **Sync from Entra…**, whose preview shows its removals. Users
  without a licence, or outside the environment's security group, still will not be added.
- **Copy provisioning script** copies an `Add-AdminPowerAppsSyncUser` PowerShell script for every
  group-only user who needs provisioning, for a Power Platform admin to run.
- **Remove leftovers…** removes team-only users one at a time. Users deleted or disabled in
  Entra, or whose Entra object id is stale — the ones the sync does not remove — are ticked. Other
  categories are listed unticked; anyone Entra says *is* a member, or who could not be checked, is
  never offered.

Graph is called as the tab's signed-in account, in the environment's tenant. The default client
is pre-consented; with your own `ClientId`, grant it `GroupMember.Read.All` and `User.Read.All`
delegated permissions.

### Queue membership sync

Pick a team and a queue, then **Preview**. Team members missing from the queue are **added**;
queue members not in the team are **removed**. Disabled users and application users in the team
are not added. Application users in the queue are listed for removal but unticked, since they are
often there for automation. Tick **Additive only** to never remove anyone: queue members who are
not in the team are then shown as *kept*, and only additions go to the confirmation. Users are
added and removed one at a time with a direct associate on
the queue membership relationship, and each reports its own result; afterwards the preview is
read again.

## Where is this used? (search inside definitions)

The search box matches metadata - names, types, owners. **Search inside** (`Ctrl+Shift+U`, on the
toolbar) answers a different question: *what refers to column `contoso_status`, variable
`contoso_ApiUrl` or connector `shared_sql`?* It searches the bodies of the components in the
loaded list:

- cloud flow definitions (`clientdata`) and classic workflow / business rule XAML;
- text web resources (scripts, HTML, CSS, XML, SVG, RESX), decoded;
- form XML, view FetchXML and layout XML, sitemap XML;
- plug-in step filtering attributes and unsecure configuration.

Each hit shows the component, which body and line, and the line around the match with the match
highlighted; double-click to open the component (flows and scripts open on their source). Bodies
are read once per tab, a chunk of rows per request, and read again only for components modified
since - so a second search is instant. **Find usages** on a table's column (Table components tab)
and on an environment variable (Value tab) runs the same search for that name.

## Solution documentation

**Document** (toolbar, with a solution selected) writes a readable inventory of the solution as
Markdown, for a handover, an audit or a wiki:

- the solution's name, version, publisher and managed state, and a count of components by type;
- **tables** - custom columns with type and requirement, relationships and alternate keys;
- **cloud flows** - on or off, trigger, connectors used, action count and a Mermaid diagram;
- **environment variables** - type, default and the value in this environment, secrets redacted;
- **plug-in steps** - message, table, stage, mode, rank and filtering attributes;
- **security roles** - their privileges on the solution's tables;
- **dependencies** on components outside the solution.

Choose the sections, and either one Markdown file or a folder with an index and one file per
component - for a docs repository. A section that cannot be read is listed under *Not documented*.

## Recent changes

**Recent changes** (sidebar) answers "something broke yesterday - what changed?". It lists every
component in the environment modified in the last 24 hours, 7 days or 30 days, newest first, with
**who modified it** for flows and workflows, web resources, forms, views, plug-in steps and
environment variables. Solution imports, upgrades and uninstalls from Solution history sit on the
same timeline, with their result and any error. Filter by type, or to *unmanaged only* - the direct
customisations that usually cause drift. Double-click a component to open it; export to CSV or
Markdown for an incident write-up.

## Overview tab for apps, agents and more

Components without a tab of their own now open on an **Overview**: their key properties, and the
parts they are made of.

| Type | Overview |
| --- | --- |
| Canvas app | Owner, type, version, last published and commit message; the connectors and connection references it uses |
| Model-driven app | Version, published date, navigation; its sitemap as area / group / item / what it opens; the components and security roles in the app |
| Copilot Studio agent | Language, authentication, access, last published; its topics, knowledge sources and actions |
| Custom API | Function or action, binding and bound table, plug-in type, run privilege; request parameters and response properties |
| Security role | Its privilege matrix by table, and its other privileges |
| Global choice | Its options with value, label, colour and description |
| Business process flow | Its stages, with category and table |

A part that cannot be read is named at the top of the tab rather than left empty.

## Dependency graph

**Explore graph…** on a component's Dependencies tab opens its dependencies as a tree in both
directions - *what depends on this* and *what this needs* - read a level at a time as each node is
expanded. Each node shows its type, whether it is managed, and whether it is **outside the
solution** being viewed, since those are the dependencies that surprise people at import.

**What breaks if I remove this?** (Removal impact tab) follows the dependents transitively, up to
four levels or 300 components, and lists everything that would be affected - what lies outside the
solution first. Double-click any component in the solution to open its details. **Copy as
Mermaid** copies the removal impact, or the tree as expanded, as a Mermaid flowchart whose arrows
read "depends on".

## Readiness check

**Readiness check** (`Ctrl+Shift+R`, in the sidebar) asks whether a solution will import cleanly
into another environment and work once it is there. Pick the environment it comes from, the
solution, and the environment it is going to. Both are only read. It reports:

- **Version** - the target already has a newer version (refused), the same one, or has the
  solution unmanaged.
- **Missing dependencies** - components the solution needs without containing them
  (`RetrieveMissingDependencies` in the source) that the target does not have either.
- **Environment variables** - no value in the target and no default to fall back on, or a target
  value identical to the source's (a dev URL that followed the solution to production).
- **Connection references** - new to the target, bound to nothing there, or bound to a connection
  in error.
- **Flows** - off in the source, or owned in the target by a disabled user.
- **Plug-in assemblies** - older in the target (updated by the import) or newer (taken back).
- **Unmanaged layers** on the solution's components in the target, which would hide the import's
  changes (up to 400 components).

Each finding is a *Blocker*, *Warning* or *Info*; double-click one to open the component's
details. An area that could not be checked is listed as such rather than passing silently.
**Export Markdown…** saves the report for a pull request or change ticket.

## Connection references and deployment settings

- **Connections tab.** A cloud flow's details list the connection references its definition uses;
  a connection reference's details show itself. Each row gives the connector, the bound
  connection, its owner and its status from Power Automate - *Connected*, *Error* with the reason,
  *No connection*, or bound to a connection not shared with you. A flow with a broken reference
  says so, since that is the usual reason a deployed flow will not turn on. *Used by* (the
  Dependencies tab) lists the flows and apps that use a reference.
- **Unbound references in the grid.** Connection references get a sub type of *Has connection* or
  *No connection*, so choosing the Connection Reference type and then *No connection* lists the
  ones still to bind.
- **Deployment settings** (toolbar) writes the file `pac solution import --settings-file` takes
  for the selected solution: each environment variable's schema name and value, and each
  connection reference's logical name, connection id and connector. It can be filled from this
  environment's current values - open the target environment's tab to take its values - or
  written as a blank template. The status line says how many blanks are left to fill in.

## Quick actions

The most common fixes after a deployment, without leaving for the maker portal:

- **Turn on / Turn off** a cloud flow, **Activate / Deactivate** a classic workflow, business rule
  or action, and **Enable / Disable** a plug-in step - from the details window, or from the grid's
  right-click menu for every selected row at once.
- **Turn on every flow listed that is off** (right-click menu) checks every cloud flow in the
  loaded list and turns on the ones that are off.
- **Set value / Remove current value** on an environment variable's *Value* tab. The value is
  checked against the variable's type first: a number, yes/no, valid JSON, a non-empty data
  source. Secrets (Key Vault references) are left to the maker portal.

Each goes through the [production guard](#the-production-guard) and is confirmed in a prompt that
names the environment and its type. Each is recorded in the run log under
`%LOCALAPPDATA%\PPObjectSearch\writes\` with the state it replaced and the write that reverses it.

## Updates

Once a day the app asks GitHub whether a newer release exists, and if so shows a link to it under
the version in the sidebar. It never downloads or installs anything. Set `"CheckForUpdates": false`
in `settings.json` to turn the check off.

## Logs

Problems the app recovers from, and any unexpected error, are written to
`%LOCALAPPDATA%\PPObjectSearch\logs` - one file a day, kept for two weeks. **Open log folder** is
in the menu behind the sun icon at the bottom of the sidebar; attach the day's file to a problem
report. An unexpected error is also shown, with an offer to copy its details.

## Authentication

By default the app uses Microsoft's pre-consented public client for Dataverse tooling
(`51f81489-12ee-4a9e-aaae-a2591f45987d`, the one PAC CLI and XrmToolBox use), so **no app
registration is needed**. If your tenant blocks it, register your own public client with the
`http://localhost` redirect URI and the Dynamics CRM `user_impersonation` delegated permission,
then set `ClientId` in settings (below).

### Government and China clouds

Environments in the US government and China clouds work like any other. The cloud is told by the
environment's host - `crm9.dynamics.com` (GCC), `crm.microsoftdynamics.us` (GCC High),
`crm.appsplatform.us` (DoD), `crm.dynamics.cn` (China) - and sign-in, Graph, Power Automate, the
Power Platform API (which the production guard asks for the environment type), global discovery
and the maker portal links all go to that cloud's own endpoints. An environment on a custom domain
takes its cloud from the sign-in authority Dataverse names when challenged, accepted only if it is
one of the known clouds' authorities. For `ppos` with a service principal in one of these clouds,
also set `PPOS_CLOUD_ENVIRONMENT` to an environment URL in it.

## Settings

`%LOCALAPPDATA%\PPObjectSearch\settings.json` — written automatically, all fields optional.
Comments and trailing commas are allowed. The file is saved through a temporary file, with the
previous version kept as `settings.json.bak`. If it cannot be read, the app says so at startup,
copies it to `settings.json.bad-<time>`, runs on defaults and does not save over it until it is
fixed and the app restarted.

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
  // environments need no entry. Matched on host; no wildcards. Set from the sidebar or by hand.
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

          // The columns chosen for comparison, recorded on save. A column the table gains later is
          // in neither list, so it is reported as new and left out until it is chosen in Settings.
          "ComparedColumns": ["contoso_code", "contoso_name", "contoso_sortorder"],

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
