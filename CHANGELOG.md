# Changelog

All notable DualLink changes are recorded here. Versions follow semantic versioning.

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
