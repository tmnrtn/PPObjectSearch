# Code signing (SignPath)

Releases are signed through [SignPath Foundation](https://signpath.org), which gives open-source
projects a free Authenticode certificate. The certificate is issued to SignPath Foundation, not to
the project: Windows shows "SignPath Foundation" as the publisher. The private key never leaves
SignPath, and it signs only what this repository's workflow built on GitHub's own runners.

[release.yml](../../.github/workflows/release.yml) makes two signing requests per build:

1. `PPObjectSearch.exe` and `ppos.exe`, with the [exes](exes.xml) artifact configuration.
2. `PPObjectSearch-Setup.exe`, built around the signed exes, with the [installer](installer.xml)
   artifact configuration.

A tag uses the `release-signing` policy, so each request waits in SignPath until it is approved
there - two approvals per release, within an hour of each other or the job fails. A manual run of
the workflow uses `test-signing`, signed straight away with SignPath's untrusted test certificate,
which checks the setup without an approval.

Without the secret and variable below, the workflow skips signing and builds unsigned, as it did
before.

## Setting it up

1. Apply at [signpath.org/apply](https://signpath.org/apply). The project must meet the
   [conditions](https://signpath.org/terms): an OSI license (MIT), no proprietary components, and
   a **Code signing policy** on the project's home page - the README has one, which names the
   team and says what the app sends over the network.
2. Once accepted, in SignPath (the organization SignPath Foundation sets up for the project):
   - Turn on multi-factor authentication. SignPath Foundation requires it for every team member,
     here and on GitHub.
   - Check the project's slug. The workflow assumes `PPObjectSearch`; for anything else, set a
     repository variable `SIGNPATH_PROJECT_SLUG`.
   - Link the project to the predefined **GitHub.com** trusted build system, and install the
     [SignPath GitHub App](https://github.com/apps/signpath) on this repository.
   - Add two artifact configurations with the slugs `exes` and `installer`, copied from the XML
     files here.
   - Check the signing policies are `release-signing` and `test-signing`.
   - Create a CI user with the **Submitter** role on both policies, and an API token for it.
3. Add to this repository (Settings > Secrets and variables > Actions):

   ```powershell
   gh secret set SIGNPATH_API_TOKEN          # the CI user's API token
   gh variable set SIGNPATH_ORGANIZATION_ID --body <organization id>
   ```

4. Run the Release workflow by hand. Its **Check signatures** step should show all three files
   signed with the test certificate.

## Cutting a signed release

Push the tag as usual, then approve the two signing requests in SignPath as the job reaches them.
The release notes start with SignPath's attribution, which SignPath Foundation asks for on the
download page.
