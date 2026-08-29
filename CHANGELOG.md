# Changelog

All notable DualLink changes are recorded here. Versions follow semantic versioning.

## 2.3.0-dev.3 - 2026-08-29

- Replaced the download-only limiter with one combined bandwidth limit shared by upload and download traffic.
- Added smoothed real-session connection latency and quality labels to route status and Smart routing decisions.
- Added plain-language checks for each selected connection, DNS, route independence, the application filter, and active session distribution.
- Added automatic adapter refresh after cable, hotspot, address, sleep, and wake network changes.
- Kept routing alive on one available connection and automatically reintroduced a recovered route for new sessions.
- Added restrained tray notifications for connection loss, recovery, and filter-service recovery.
- Hid technical activity behind an explicit control while keeping diagnostic results readable by default.
- Added a compact one-minute Ethernet and Wi-Fi traffic history without additional network probes.
- Added manual Stable and Preview update checks; updates never download or install silently.
- Made the traffic history time-based while minimized and made settings updates crash-safe through same-directory atomic replacement.
- Preserved temporarily disconnected adapter choices and route weights so automatic recovery cannot be undone by another settings change.
- Added smoothed per-route connection reliability so Smart routing remembers intermittent failures after a short quarantine expires.
- Cancelled and drained the opposite relay as soon as either side of a proxied connection closes, preventing idle transfer tasks and rented buffers from lingering.
- Made the tray quality summary report unstable, fair, and slow routes instead of describing every available route as healthy.
- Hardened the emergency watchdog so it rejects corrupted or redirected recovery paths before touching the filter service or configuration files.
- Stopped destination refusals and app shutdown cancellation from lowering a connection's reliability or falsely quarantining a healthy route.
- Added bounded retry backoff after controller failures to avoid rapid filter-service churn, while allowing an explicit re-arm to retry immediately.
- Corrected the active status when only Ethernet or only Wi-Fi is currently available, and surfaced restoration failures without crashing the UI event loop.
- Ensured Exit still completes when foreground restoration fails so the independent watchdog can perform its recovery pass instead of leaving a half-closed window.

## 2.3.0-dev.2 - 2026-08-29

- Added Smart, Balanced, and Backup connection behaviors with live switching.
- Added per-route load tracking, failure cooldown, and automatic healthy-link failover.
- Secured the local SOCKS endpoint with random per-session username/password authentication and a dynamic loopback port.
- Bounded local proxy concurrency and added a handshake deadline to resist stalled-client resource exhaustion.
- Matched custom applications and the detected browser by full executable path in addition to process name.
- Replaced the second full WPF recovery process with a trimmed, self-contained watchdog; measured working set fell from about 146 MB to 23 MB.
- Reduced background process scanning and slowed refresh work while the window is hidden.
- Disabled ReadyToRun for the desktop publish to favor package and working-set efficiency.
- Constrained elevated recovery to the expected DualLink and ProxiFyre paths and made active config writes atomic.
- Added deterministic, privacy-safe UI snapshots plus Windows CI, CodeQL, Dependabot, and an attested manual candidate build.
- Replaced the letter-based shell icon with an original two-routes-to-one confluence mark and exact multi-resolution Windows ICO frames.
- Added a branded notification-area menu plus live hover details for combined download, upload, active sessions, and routing mode.
- Expanded integration tests for proxy authentication, failed-route quarantine, and full-path matching.
- Clarified the SOCKS credential gate and added malformed-protocol regression coverage for a clean CodeQL security scan.

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
