# TRACKER — Application Insights in PPObjectSearch

## Goal

Show the telemetry an environment sends to Azure Application Insights inside the app, so failures
can be looked into without leaving it:

- **Failures window** — App Insights becomes a fourth source beside cloud flow runs, classic
  workflow jobs and the plug-in trace log, with the same period, trend, by-component and by-error
  views, CSV/Markdown export and solution scoping.
- **Object details** — an *App Insights* tab for objects with telemetry, starting with plug-in
  types and steps (their exceptions and executions); cloud flows follow once flow export is supported.
- **Per-environment resource** — the App Insights resource is picked from the Azure subscriptions
  the tab's signed-in account can see (Azure Resource Manager), and saved per environment like the
  browser profile.

First supported telemetry: the **Power Platform admin center Dataverse data export**, which is the
only export configured today. The model must leave room for Power Automate export, canvas app
telemetry (each app's own connection string) and custom SDK telemetry without being reworked.

## Constraints

- Read-only. Nothing writes to Azure or to the environment.
- Sign-in reuses the tab's `EnvironmentAuthContext`: the same account and tenant, and a token for
  each extra resource, as `PowerAutomateClient` and `GraphClient` already do.
- Sovereign clouds: every Azure endpoint comes from `Core/Clouds.cs`, never hard-coded.
- Tests use `FakeHttpHandler` (`tests/PPObjectSearch.Tests/Infrastructure`). No test touches the
  network. Fixtures are shaped on real responses captured at the schema checkpoint.
- Real Azure and tenant checks can only be done on the user's Windows machine. Those steps are
  marked **[USER]** and their results are written into *Findings* below before work continues.
- Follow the repo's conventions: a partial or client class per data source, a `required`-init model,
  a pure service for shaping data, plain-English notes when a source can't be read, README sections,
  and a version bump in the final step.

## Open questions (answered by the checkpoints)

1. Can the default public client `51f81489-…` get delegated tokens for Azure Resource Manager and
   for an Azure Monitor query API (`api.applicationinsights.io` or `api.loganalytics.io`)? If not,
   the existing `ClientId` setting (own app registration) is the route, and the error must say so.
2. Which query endpoint: the App Insights API by app id (classic table names `exceptions`,
   `dependencies`, `requests`, `traces`) or Log Analytics by resource or workspace (`AppExceptions`,
   …)? Pick whichever the token allows. Prefer the resource-scoped one, so *Reader* on the App
   Insights resource is enough.
3. What does the Dataverse export actually write? Which table and which `customDimensions` hold
   the plug-in type, step, message, entity and correlation id, and the organization/environment id
   (one App Insights resource can receive several environments' export)?
4. GCC: is the App Insights resource in Azure commercial or Azure Government? (This affects which
   ARM/query endpoints the `UsGcc` cloud gets.)

## Steps

### Phase 0: Spike on access (blocks everything else)

- [x] **0.1 Azure endpoints in `Cloud`.** Add `ResourceManager`, `ResourceManagerResource`,
      `MonitorQueryApi` and `MonitorQueryResource` (App Insights API and Log Analytics API, both
      kept until 0.3 decides), and `AzurePortal`, to the `Cloud` record and to every cloud in
      `Core/Clouds.cs`. Tests: every cloud has them; government/China hosts are the sovereign ones.
  - `Core/Clouds.cs`: new `AzureEndpoints(ResourceManager, AppInsightsApi, LogAnalyticsApi, Portal)` record as a required `Cloud.Azure` member, so each base URL is also its token resource and the separate `*Resource` fields aren't needed. Public/GCC → commercial Azure (open question 4 assumed), GCC High/DoD → Azure Government, China → Azure China. Test in `CloudsTests.cs`.
- [x] **0.2 `AzureMonitor/AzureAccessProbe`.** This is a small diagnostic, not throwaway. For each
      Azure resource it tries `EnvironmentAuthContext.GetTokenAsync` and then one cheap call:
      `GET subscriptions` on ARM, plus a trivial query when a resource id is given. It returns a
      result per resource: OK, an AADSTS code and message, or an HTTP status. An *Azure access…* item
      in the environment's sidebar context menu (`MainWindow.xaml` and `EnvironmentSessionView.xaml`,
      beside *Open links in*) shows the result in a copyable message and writes it to the log.
      Tests: AADSTS65001/65002-style failures and 401/403 become the right plain-English results.
  - New `AzureMonitor/AzureMonitorClient.cs` (token classification into `AzureFailure`, ARM subscription/component listing with paging, App Insights query by app id returning `TelemetryTable`) and `AzureAccessProbe.cs`; `DataverseClient.CreateAzureMonitorClient()` passes the test handler through. *Check Azure access…* is in both environment menus (`EnvironmentSessionViewModel.Telemetry.cs`) and shows the results in the Ask dialog with *Copy results*. ARM listing and query were built here too, ahead of 1.2/2.2, because the probe needs them.
- [ ] **0.3 [USER] Run the probe on Windows** against the environment with the Dataverse export,
      first with the default client, then (only if that fails) with a `ClientId` app registration.
      Record which tokens and calls succeed, with error codes, under *Findings*. Then decide
      questions 1 and 2, and remove the losing query endpoint from 0.1.

### Phase 1: Resource picker and per-environment setting

- [x] **1.1 Settings model.** Add `AppSettings.AppInsights: Dictionary<host, List<TelemetryBinding>>`.
      `TelemetryBinding` holds `ResourceId`, `AppId`, `Name`, `SubscriptionName` and a
      `TelemetryKind` enum (`DataverseExport` now; `PowerAutomateExport`, `CanvasApp` and `Custom`
      reserved). Use a list per environment so later kinds and several canvas apps fit without a
      schema change. `Get`/`Set` follow the browser-profile pattern; the dictionary is re-keyed
      case-insensitively on load. Tests: round-trip, re-keying, clearing, unknown kinds kept.
  - `Models/Telemetry.cs` (`TelemetryKind`, `TelemetryBinding` with `Kind` stored as text and `KnownKind` parsed from it) and `AppSettings.AppInsights` with `Get/SetTelemetryBinding`; an emptied section is dropped. Tests in `tests/.../AppInsights/TelemetrySettingsTests.cs`.
- [x] **1.2 ARM listing in `AzureMonitorClient`.** Add `ListSubscriptionsAsync` and
      `ListAppInsightsComponentsAsync`. Use ARM `providers/Microsoft.Insights/components` per
      subscription, or one Resource Graph query if 0.3 shows it's allowed. Handle paging via
      `nextLink`, return name, resource group, location, app id and workspace id, and skip
      subscriptions that refuse with a note. Tests against the fake, including paging and a 403
      subscription.
  - Done in 0.2 (`ListSubscriptionsAsync`, `ListComponentsAsync`, `ListAllComponentsAsync`, tested in `AzureMonitorClientTests`). Per-subscription ARM listing rather than Resource Graph, since it needs nothing beyond Reader.
- [x] **1.3 Picker window.** Add *App Insights resource…* to the environment's sidebar context menu.
      It opens a list of components (filterable by name or subscription) with the current choice
      ticked, plus *None*. Saving stores a `DataverseExport` binding for the environment. The
      session exposes `AppInsightsBinding` / `HasAppInsights` and raises changes like
      `RaiseBrowserProfile`. Tests: view model selection, save and clear (Windows CI for the VM tests).
  - `ViewModels/AppInsightsPickerViewModel.cs` + `Views/AppInsightsPickerWindow.xaml`, opened from *App Insights resource…* in both environment menus; the session (`EnvironmentSessionViewModel.Telemetry.cs`) exposes `AppInsightsBinding`, `HasAppInsights`, `TelemetryScope`, `AppInsightsChanged`, and keeps `OrganizationId` from RetrieveCurrentOrganization. Added `TelemetryBinding.OnlyThisEnvironment` (a picker checkbox) as an escape hatch in case the environment filter does not match the real export. VM tests in `tests/.../ViewModels/AppInsightsPickerViewModelTests.cs` (Windows CI only).
- [x] **1.4 Connection check.** After a pick, run one query (`<table> | take 1` over the last day)
      and count rows for this environment's organization id. The picker then says either "Receiving
      telemetry from this environment" or "Nothing from this environment in the last day — is the
      export pointed here?". Uses the filter from 2.2.
  - Selecting a resource (or toggling the checkbox) runs `TelemetryProfiles.CheckAsync` - one `union … | summarize Total, Matching` over the last day - and `TelemetryCheck.Describe` words the result; the newest pick cancels an older check.

### Phase 2: Query layer and the Dataverse export schema

- [ ] **2.1 [USER] Capture the real schema.** In the portal, run the KQL listed under *Findings → schema
      capture* (a few rows each of exceptions, plug-in dependencies, failed requests, with
      `customDimensions`), redact anything sensitive, and paste the results. Turn them into
      JSON fixtures under `tests/PPObjectSearch.Tests/AppInsights/`. This answers question 3.
- [x] **2.2 `AzureMonitorClient.QueryAsync`.** POST a KQL query with a `timespan`, get a typed table
      back (columns plus rows, with `dynamic` columns parsed), and cancel through the token. Cap rows
      with `| take` (reuse `DataverseClient.MaxFailureRows` semantics) and report when the result was
      cut short. API errors (`BadArgumentError`, throttling, partial results) become messages. Use the
      shared `RetryHandler` pipeline and the internal handler constructor for tests. Tests: parsing,
      partial-error note, 403 message, cancellation.
  - Built in 0.2; the row cap (`| take 5000`) and the "cut short"/partial-result notes live in `DataverseExportProfile.ReadFailuresAsync`. Query goes to the App Insights API by app id with classic table names (pending confirmation at 0.3).
- [x] **2.3 `TelemetryProfile` per `TelemetryKind`.** The KQL templates and column mapping for one
      kind of export, kept pure and testable. `DataverseExportProfile` gives the
      failures query (plug-in exceptions and failed Dataverse requests), filtered to the
      environment's organization id. It also gives the per-plug-in queries (exceptions; executions
      with count, failures and p95 duration). The other kinds are left as stubs only, so adding them
      later means adding a profile and nothing else. Tests: generated KQL for the given time,
      environment and scope. KQL string literals are escaped (no injection from names).
  - `AzureMonitor/TelemetryProfiles.cs` (`ITelemetryProfile`, `TelemetryScope`, environment filter matching org id / env id / host as text in customDimensions, cloud_RoleInstance and cloud_RoleName) and `DataverseExportProfile.cs` (failed plug-in dependencies, exceptions and failed requests joined in C# by operation/parent id; plug-in summary query). `Kql.Literal` escapes values. Fixtures in `tests/.../AppInsights/TelemetryFixtures.cs` are provisional until 2.1.

### Phase 3: App Insights in the Failures window

- [x] **3.1 Model.** Add `FailureSource.AppInsights` with a `TelemetryKind` and an `OperationId` /
      `ItemId` on `FailureEvent` for linking. Set `SourceLabel` to "App Insights" and `StepLabel` to
      the plug-in message and entity or the request operation. The component key is the plug-in type
      name (the key the trace log uses) or the request's operation name. Add `FailureData.TelemetryRowsRead`.
  - `Models/Failures.cs`: `FailureSource.AppInsights`, `FailureEvent.IsPlugin/OperationId/Details/AlsoInTraceLog`, `FailureData.TelemetryRowsRead`, `FailureQuery.AppInsights`; request and exception component keys are prefixed `request:`/`exception:` so they never collide with a plug-in type name. `DetailsTab.Telemetry` added here as OpenEvent targets it.
- [x] **3.2 Reading.** `AppInsightsFailures.ReadAsync(binding, profile, from, to, query, data)` runs
      next to `DataverseClient.GetFailuresAsync`. Its failures go through the same `Source(...)` note
      handling, so a missing resource, a refused token or a 403 becomes a note rather than an error.
      Solution scoping maps `FailureQuery.PluginTypeNames` into the KQL. Tests against the fake with the
      2.1 fixtures, including scoping and the notes.
  - `AzureMonitor/AppInsightsFailures.ReadAsync` runs after `GetFailuresAsync` in `FailuresViewModel.LoadAsync` whenever a binding exists (every source is read; pills only filter). Failures to read, and kinds without a profile, become notes. Tests in `AppInsightsFailuresTests.cs`.
- [x] **3.3 Window.** Add an *App Insights* include toggle in `FailuresViewModel`, shown only when the
      environment has a binding. Without one, show a hint linking to the picker. `EmptyText` and the
      notes pick it up. `OpenEvent` on an App Insights event opens the plug-in's details
      (via `ItemFor`), or failing that the Azure portal: a Logs deep link scoped to the resource,
      querying the event's operation id. Exports include the new source. Tests: VM toggling,
      component grouping, export rows.
  - *App Insights* pill (shown only with a binding) and an *Add App Insights…* link otherwise; the window re-reads when the session raises `AppInsightsChanged`. A plug-in failure opens the loaded plug-in on its App Insights tab; a request/exception opens its operation in the portal Logs (`AzureMonitor/AppInsightsLinks.cs`, gzip+base64 share-link format - verify at 5.3). CSV export gains Operation and Also-in-trace-log columns. VM tests in `tests/.../ViewModels/FailuresAppInsightsTests.cs`.
- [x] **3.4 Overlap with the trace log.** When both sources report the same plug-in failure (same
      type, within a second, same message), keep both but mark the App Insights one, so the counts
      on the summary band are honest. Say so in the notes, for example "n failures also in the
      trace log". Tests on `FailureOverview`.
  - `FailureOverview.MarkTraceLogOverlaps` (same type, within 2 s, one-to-one) and `FailureOverview.Counted` (drops marked failures only while trace-log failures are shown); `TotalFailures` and the Markdown total use it, and a note says how many overlapped.

### Phase 4: App Insights tab in object details

- [ ] **4.1 Tab plumbing.** Add `DetailsTab.Telemetry` ("App Insights"). `DetailsTabs.TabsFor` and
      `ShortcutsFor` take whether the environment has a binding, and add the tab for
      `ObjectKind.Plugin` only when it does (more kinds come later via the profile). Tests in
      the existing `DetailsTabs` tests.
- [ ] **4.2 Content.** For a plug-in type or step, show executions, failures and p95 duration over
      a period picker, plus the latest exceptions (time, message, entity, operation id) with the full
      exception text in a code view. Add an *Open in Azure portal* link. Load lazily when the tab is
      first shown and cancel on close, as the trace log does. Tests: the view model with the fake.

### Phase 5: Finish

- [ ] **5.1 Errors and auth fallback.** Turn the outcomes from 0.3 into the final messages: missing
      Azure RBAC (Reader or Monitoring Reader on the resource), a token refused for the default client
      (pointing to the `ClientId` app registration and its permissions), and a resource deleted or
      moved (offer to pick again). Each is logged via `Log`.
- [ ] **5.2 Docs.** Add a README feature bullet and an *Application Insights* section: what's read,
      required Azure roles, choosing the resource, sovereign clouds, and the app-registration
      permissions if 0.3 needs them. Document `AppInsights` under *Settings* and add the Azure
      permissions to *Authentication*.
- [ ] **5.3 [USER] End-to-end check on Windows** against the real environment: picker, Failures with
      and without a solution, a plug-in's tab, portal links, and a user without Azure access.
- [ ] **5.4 Release.** Run the full test suite on Windows CI, check SonarQube is clean, bump the version
      (minor) and open the PR.

### Later (not in this plan)

Power Automate export profile (flow runs and actions; the App Insights tab on cloud flows), canvas
app bindings (one per app, matched by app id), custom SDK telemetry with saved KQL, and a
`ppos` command for App Insights failures.

## Findings

_Results from the [USER] checkpoints go here._

### 0.3 Access probe

- Default client, ARM: _
- Default client, App Insights API: _
- Default client, Log Analytics API: _
- Own app registration (only if needed): _
- Decision on question 1 and question 2: _

### 2.1 Schema capture

Run in the App Insights resource's *Logs* over the last 7 days, then paste the output (redacted):

```kusto
exceptions | take 5 | project timestamp, type, outerMessage, problemId, operation_Id, operation_Name, cloud_RoleName, cloud_RoleInstance, customDimensions
dependencies | summarize n = count() by type | order by n desc
dependencies | where type has "plugin" | take 5 | project timestamp, name, type, target, success, resultCode, duration, operation_Id, customDimensions
requests | where success == false | take 5 | project timestamp, name, resultCode, duration, operation_Id, customDimensions
```

- Organization/environment id field: _
- Plug-in type / step / message / entity fields: _
