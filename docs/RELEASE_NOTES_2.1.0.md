# DualLink 2.1.0

This release gives DualLink a calmer, more deliberate Windows interface inspired by high-quality desktop network utilities. Connection controls stay in a compact utility pane, targets share one clean application surface, and technical details remain hidden until requested.

## Highlights

- Live combined Ethernet and Wi-Fi throughput
- Per-route weights, including an `Off` state for uninterrupted single-link switching
- Automatic default-browser discovery
- Application-scoped boosting with automatic start and recovery
- Close-to-tray operation and fail-safe routing restoration
- Offline x64 installer with bundled prerequisites
- SPDX 2.3 SBOM and SHA-256 checksums
- AGPL-3.0 source release with privacy, security, and third-party notices

## Important licensing note

The bundled Windows Packet Filter driver is free for personal, educational, and nonprofit use. Commercial distribution or use requires appropriate licensing from NT Kernel Resources. The current installer was compiled with the non-commercial Inno Setup distribution; commercial distributors must also obtain the applicable Inno Setup license.

## Installation

Download `DualLink-2.1.0-Setup-x64.exe` and run it as an administrator. This release is not code-signed, so Windows may show a SmartScreen warning. Verify the download against `SHA256SUMS.txt` before installation.
