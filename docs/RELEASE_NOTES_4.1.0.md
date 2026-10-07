# DualLink 4.1.0

DualLink 4.1.0 simplifies the main screen and finds supported apps installed on your PC. It also includes the v4 helper architecture, routing recovery, and verified update flow that were not in the previous public release, 3.1.1.

## Changes

- Short labels, a compact links and apps view, and settings and details that open when needed.
- Automatic app scanning with real installed icons. Missing launchers stay out of the list; Scan and Add remain available.
- Background discovery and network sampling, cached icons, and a virtualized app list.
- Better helper recovery, settings rollback, watchdog reporting, traffic totals, DNS fallback, and TCP half-close handling.
- The desktop runs with normal permissions. Starting routing elevates only the local helper; an independent watchdog restores filtering after an unexpected exit.
- Shared routing code and regression checks for the controller, local IPC, recovery, limits, and app matching.

## Install

Download **DualLink-4.1.0-Setup-x64.exe** for Windows 10/11 x64. It includes the runtime and filter prerequisites. Existing app choices and preferences are retained. The other two release files are the SHA-256 checksum manifest and SPDX software bill of materials.

## Validation and limits

The local integration and window suites contain 40 checks. A user-provided live test screenshot records Edge selected with traffic carried by Ethernet and Wi-Fi. This is not a controlled speed benchmark; results depend on the server and number of connections.

Routing covers selected-app IPv4 TCP. UDP/QUIC is outside the routed path, and one TCP connection cannot combine both links. Broader launcher compatibility, physical driver recovery, sleep/wake, network removal, and multi-hour transfers still need hardware coverage. Current live speed counters are per route, not individual-app measurements.

The executables are unsigned. The bundled packet-filter driver is for personal, educational, and nonprofit use; commercial use requires the appropriate license.
