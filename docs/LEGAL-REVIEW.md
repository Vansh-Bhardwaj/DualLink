# Release compliance review

Reviewed: 2026-08-28

This checklist documents the release posture; it is not legal advice.

## Project license

DualLink source code is released under AGPL-3.0-only. This aligns the public project with the AGPL-licensed ProxiFyre component while preserving the fact that ProxiFyre is installed and operated as a separate program.

## Bundled components

| Component | Version | Release treatment |
| --- | --- | --- |
| DualLink | current | AGPL-3.0-only; source in this repository |
| ProxiFyre | 2.5.0 | Unmodified MSI; AGPL license and exact source link included |
| Windows Packet Filter driver | 3.6.2.1 | Unmodified MSI; personal/educational/nonprofit limitation disclosed |
| ndisapi source/interface | 3.6.2 | MIT notice included |
| .NET Runtime | 10.0.11 | MIT license and complete third-party notices included |
| Visual C++ Redistributable | 14.44.35211.0 | Unmodified redistributable; Microsoft terms linked |
| Inter | 4.1 | Embedded static fonts; SIL Open Font License 1.1 included |
| Inno Setup | 6.7.3 build tool | Not redistributed; noncommercial release posture disclosed |

## Completed release controls

- Root project license and third-party notices.
- Exact ProxiFyre corresponding-source link and full AGPL text.
- Separate Windows Packet Filter binary-license disclosure.
- .NET runtime license and complete third-party notices.
- Privacy and security policies.
- Installer notice shown before installation.
- SPDX 2.3 SBOM and SHA-256 release checksums.
- No bundled secrets, telemetry SDK, proprietary font, or third-party artwork. The embedded Inter files are open-source and their OFL license is included.
- Administrator/service/driver behavior disclosed.
- Unsigned-binary warning disclosed.

## Before commercial distribution

Obtain commercial rights for the Windows Packet Filter driver and Inno Setup, review Microsoft redistributable terms for the distributing organization, arrange Authenticode code signing, and obtain qualified legal review for the intended jurisdictions and business model.
