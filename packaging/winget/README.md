# winget

Manifests for the Windows Package Manager, so that on Windows the install is

```powershell
winget install konradcinkusz.CopilotScope
winget upgrade konradcinkusz.CopilotScope
```

and nothing is piped into a shell. winget downloads the release archive, checks it against the
`InstallerSha256` in the manifest, extracts it under its own packages directory and adds the
extracted folder to the PATH (`ArchiveBinariesDependOnPath`). The archive is the same `copilotscope-win-<arch>.zip` the
GitHub Release carries, checksummed in `SHA256SUMS` and attested by the release workflow; the
manifest adds a second, independently held copy of the hash.

## Layout

`manifests/k/konradcinkusz/CopilotScope/<version>/` holds the three files the community
repository expects — version, installer and default locale — in the schema version named in
each file. The installer manifest is a `zip` with a nested `portable`: the archive unpacks to
`copilotscope-win-<arch>/`, and the binary serves the dashboard from the `wwwroot` beside it,
which is why the whole folder is installed and put on the PATH as it is
(`ArchiveBinariesDependOnPath: true`), rather than a symlink to the binary alone.

## First submission, by hand

`wingetcreate update` can only update a package that already exists in
[microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs), so the first version is
submitted once, by a person, from a Windows machine:

```powershell
winget validate --manifest packaging/winget/manifests/k/konradcinkusz/CopilotScope/1.2.0
winget install  --manifest packaging/winget/manifests/k/konradcinkusz/CopilotScope/1.2.0
winget install Microsoft.WingetCreate
wingetcreate submit packaging/winget/manifests/k/konradcinkusz/CopilotScope/1.2.0
```

`wingetcreate` signs in to GitHub when it runs, or reads `WINGET_CREATE_GITHUB_TOKEN` from the
environment; the token goes on no command line
([token.md](https://github.com/microsoft/winget-create/blob/main/doc/token.md)). The same three
files can instead go into a pull request against `microsoft/winget-pkgs` by hand, under
`manifests/k/konradcinkusz/CopilotScope/1.2.0/`, titled `New package: konradcinkusz.CopilotScope
version 1.2.0` and touching nothing else. The community pipeline validates the manifest, checks
the hash, scans the archive and installs the package on a clean Windows image; unsigned binaries
are accepted.

## Every release after that

`.github/workflows/winget.yml` runs from the native release workflow once the archives are
attached (a `release: published` trigger never fires for a release the workflow's own token
created), and by hand for any existing tag. With the repository secret
`WINGET_CREATE_GITHUB_TOKEN` set (a classic PAT with `public_repo`, owned by whoever submits),
it checks that both Windows archives are attached, then runs `wingetcreate update` with their
URLs and opens the update pull request against the community repository. Without the secret it
does nothing and says so — the manifests here are then updated by hand, the same way as above.

The manifests in this folder are the record of what was submitted. `wingetcreate` writes the
same three files, and the workflow keeps them as the run's `winget-manifests-<version>`
artifact; copy them here after a submission so the history stays in the repository.
