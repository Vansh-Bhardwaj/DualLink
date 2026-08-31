# Local release workflow

This workflow keeps preview and stable packaging reproducible on Windows. It does not create Git tags, push to a remote, publish a GitHub Release, start DualLink, or request administrator approval.

## Version and branch map

| Work | Branch | Informational version | Tag when reviewed |
| --- | --- | --- | --- |
| Alpha | `v4/alpha` | `4.0.0-alpha.N` | `v4.0.0-alpha.N` |
| Beta or release candidate | `v4/beta` | `4.0.0-beta.N` or `4.0.0-rc.N` | matching `v4.0.0-...` |
| Stable | `main` or `v4/stable` | `4.0.0` | `v4.0.0` |

The numeric `VERSION` file remains the base version for every preview. `Directory.Build.props` carries the exact preview or stable identity used by the executable, installer, SBOM, and package name.

## Prepare a checkpoint

From the repository root:

```powershell
pwsh -NoProfile -File .\tools\Set-Version.ps1 -Version 4.0.0 -Stage Alpha -Number 3 -EnforceBranch
```

Use `-Stage Beta` or `-Stage ReleaseCandidate` on `v4/beta`, or `-Stage Stable` on `main` after the stable gate is complete. Add `-WhatIf` to preview the metadata change without writing either version file.

## Build locally

Run the matching stage with branch enforcement enabled:

```powershell
pwsh -NoProfile -File .\build.ps1 -Stage Alpha -EnforceBranch
pwsh -NoProfile -File .\build.ps1 -Stage Beta -EnforceBranch
pwsh -NoProfile -File .\build.ps1 -Stage Stable -EnforceBranch
```

`Auto` is the default stage and derives the stage from `InformationalVersion`. Stable packaging refuses `-SkipTests` and `-SkipInstaller`. Evaluation-only builds may use either switch, but they are not stable-release candidates.

Before a build, use the no-side-effect gate to validate metadata and all offline installer inputs:

```powershell
pwsh -NoProfile -File .\build.ps1 -ValidateOnly -Stage Alpha -EnforceBranch
```

## Verify the package

When the installer is built, `dist\release` is cleaned and must contain exactly these three project-owned assets:

1. `DualLink-VERSION-Setup-x64.exe`
2. `SHA256SUMS.txt`
3. `DualLink.spdx.json`

The build verifies the SBOM identity, including the privileged service payload, recomputes both SHA-256 entries, checks the installer payload and maintenance hooks, and fails if any extra asset is present. The application, privileged service, and watchdog executables remain internal under `dist\publish` and are delivered through the installer only.

The same checks can be run without building:

```powershell
pwsh -NoProfile -File .\tools\Test-Release.ps1 `
  -ReleaseDirectory .\dist\release `
  -Version 4.0.0-alpha.2 `
  -InstallerScriptPath .\installer\DualLink.iss
```

## Installer maintenance checks

The installer keeps one stable AppId and preserves the selected install folder, Start menu group, and shortcut choices. Its maintenance page offers:

- Update or reinstall: writes the new package into the existing installation.
- Repair: rewrites the application, privileged service, and watchdog payloads without changing the chosen location.
- Uninstall: runs the registered uninstaller and stops the setup flow after it completes.

To inspect the installed registration and payload without launching setup, changing files, touching the registry, or requesting elevation:

```powershell
pwsh -NoProfile -File .\tools\Test-Release.ps1 `
  -ReleaseDirectory .\dist\release `
  -Version 4.0.0-alpha.2 `
  -CheckInstalled
```

Pass `-RequireInstalled` when the check is being used as a gate on a disposable test machine. It reports the registered uninstaller and required payload files; it never performs update, repair, or uninstall itself. Those three flows still require a manual Windows test before a stable release.

## Stable handoff

After the stable gate in [RELEASE-POLICY.md](RELEASE-POLICY.md) is satisfied, review the three files in `dist\release`, verify the installed executable hash and informational version, and only then create the annotated tag and remote Release manually. The local scripts intentionally stop before those remote actions.
