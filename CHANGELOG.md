# Changelog

All notable DualLink changes are recorded here. Versions follow semantic versioning.

## 2.2.0 - 2026-08-29

- Embedded the Inter 4.1 type family and retuned hierarchy, weights, line spacing, and text colors for calmer desktop reading.
- Fixed adapter dropdown entries displaying `DualLink.LinkInfo`; network choices now show human-readable names and descriptions.
- Added connected Wi-Fi SSID discovery, so hotspot and wireless choices use the actual network name.
- Added live upload usage alongside download usage for each link and for the combined connection mix.
- Added a live download speed limiter with simple 25–300 Mbps presets to leave bandwidth for streaming, calls, and browsing.
- Replaced technical route multipliers with plain-language shares and clearer connection copy.
- Added repository screenshots, an architecture visual, and a limiter timing integration test.

## 2.1.0 - 2026-08-28

- Reworked the interface into a quiet utility pane and a single application list with clearer hierarchy, restrained color, and native-feeling controls.
- Added live combined throughput and kept diagnostics, IP addresses, and activity logs in an on-demand drawer.
- Added an AGPL-3.0 project license, privacy and security policies, installation disclosure, corrected third-party notices, SPDX SBOM, and release checksums.
- Documented the Windows Packet Filter driver and Inno Setup restrictions that apply before commercial distribution.
- Preserved live route switching, automatic browser discovery, tray behavior, watchdog recovery, and 2.0.1 service recovery.

## 2.0.1 - 2026-08-28

- Fixed a false green “BOOSTING” state when the application filter service had stopped.
- Added a two-second health check and automatic filter-service recovery.
- Serialized timer, target-selection, arm/disarm, and shutdown operations to prevent service stop/start races.
- Remembered the armed state across updates and restarts; explicit Exit and Restore still clears it.

## 2.0.0 - 2026-08-28

- Rebuilt the interface around two focused surfaces: connection mix and applications.
- Moved IP addresses, filter state, and activity logs into an on-demand Details drawer.
- Added automatic discovery of the Windows default browser.
- Added live route updates, including a zero-weight/off state for either link.
- Added close-to-tray behavior with Open, Arm/Disarm, and Exit/Restore actions.
- Added fail-safe restoration on exit and watchdog recovery after an unexpected stop.
- Added a self-contained win-x64 publish profile and offline Windows installer.
- Added an integration test for dual-link rotation and live single-link switching.
