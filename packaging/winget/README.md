# winget manifests

Two portable packages, one per released exe:

| Package id | Installs | Command |
| --- | --- | --- |
| `TomNorton.PPObjectSearch` | `PPObjectSearch.exe`, the desktop app | `PPObjectSearch` |
| `TomNorton.PPObjectSearch.CLI` | `ppos.exe`, the command line | `ppos` |

The folders here mirror [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs), for
reference: they are the manifests for 1.13.1, the first version submitted. They are not updated
for later releases - the copies in winget-pkgs are the real ones.

## First submission (by hand)

`wingetcreate update` can only update a package winget-pkgs already has, so each package's first
version is submitted from your own GitHub account with
[wingetcreate](https://github.com/microsoft/winget-create):

```powershell
winget install Microsoft.WingetCreate
wingetcreate submit packaging\winget\TomNorton.PPObjectSearch\1.13.1
wingetcreate submit packaging\winget\TomNorton.PPObjectSearch.CLI\1.13.1
```

Each opens a pull request on winget-pkgs. Check a folder first with
`winget validate --manifest <folder>`.

## Later releases

The `winget` job in [release.yml](../../.github/workflows/release.yml) runs `wingetcreate update`
for both packages on every `v*` tag, opening the winget-pkgs pull requests itself. It needs a
repository secret named `WINGET_TOKEN`: a classic personal access token with the `public_repo`
scope (wingetcreate does not support fine-grained tokens). Without the secret the job does
nothing, and it skips a package winget-pkgs does not have yet.
