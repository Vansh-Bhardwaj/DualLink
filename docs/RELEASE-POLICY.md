# DualLink version and release policy

DualLink separates development history from public distribution. A tag is a checkpoint; a GitHub Release is a supported product milestone.

## Commits

- Commit each coherent feature, fix, security change, or build-system update.
- Keep `main` buildable. Do not combine unrelated changes merely to make a larger commit.
- A commit does not imply that an installer is ready for users.

## Development checkpoints

- Use annotated tags in the form `vMAJOR.MINOR.PATCH-dev.N`, for example `v2.3.0-dev.3`.
- `Directory.Build.props` must carry the matching `InformationalVersion` without the leading `v`.
- Development tags do **not** receive a GitHub Release, release notes page, or “latest” status.
- Preview update checks may discover these tags, but DualLink never installs them silently.
- The manual **Build candidate** workflow may produce an attested artifact for evaluation. It has read-only repository contents permission and cannot publish a Release.

## Stable releases

A stable tag uses `vMAJOR.MINOR.PATCH`. Create a GitHub Release only for a substantial, user-ready milestone after every gate below is satisfied:

1. The worktree is clean and the stable tag points to the reviewed commit.
2. Version metadata, changelog, release notes, screenshots, and installer identity agree.
3. Integration, failure-recovery, limiter, authentication, and application-matching tests pass.
4. The main window, Details, Settings, network picker, and tray menu pass visual review at the minimum and reference sizes.
5. Sleep/wake, cable removal, hotspot loss, address changes, one-link continuity, and restoration are exercised on Windows.
6. The complete offline installer builds, installs, upgrades, launches, disarms, exits, and uninstalls without leaving filtering active.
7. The SBOM, third-party notices, licenses, privacy statement, security policy, and SHA-256 checksums are current.
8. GitHub Windows CI and CodeQL pass, with no unresolved security alert accepted silently.
9. The installed executable matches the tagged build by hash and embedded informational version.

### Public assets

Each GitHub Release uploads exactly three project-owned assets from `dist\release`:

1. `DualLink-VERSION-Setup-x64.exe` — the complete offline installer and the only download intended for normal users.
2. `SHA256SUMS.txt` — SHA-256 entries for the installer and SBOM.
3. `DualLink.spdx.json` — the software bill of materials for security and compliance review.

Do not upload the standalone application or watchdog executables. They are internal installer components and presenting them separately is confusing. GitHub additionally generates its own source-code `.zip` and `.tar.gz` links; those are not project-uploaded assets.

If any gate is incomplete, use another commit and development tag. Do not create a small GitHub Release as a workaround.

## Version files

- `VERSION`: numeric installer/stable target such as `2.3.0`.
- `Directory.Build.props` `VersionPrefix`, `AssemblyVersion`, and `FileVersion`: numeric Windows-compatible version.
- `Directory.Build.props` `InformationalVersion`: exact stable or development identity such as `2.3.0-dev.3`.
- `CHANGELOG.md`: one section for every development checkpoint or stable release.

The `Tag policy` workflow rejects malformed tags and tags whose version does not match the repository metadata. It validates identity only; it never creates a Release.

Local development installs may use `build.ps1 -SkipTests`, but the script marks that output as ineligible for a stable GitHub Release. Stable release builds must run the complete build without this switch.
