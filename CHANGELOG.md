# Changelog

All notable DualLink changes are recorded here. Versions follow semantic versioning.

## 3.1.1 - 2026-08-31

- Fixed nearby Wi-Fi names losing their first two characters by matching the native Windows structure layout exactly.
- Wait for Windows to finish each requested Wi-Fi scan before displaying results, preventing stale or incomplete network lists.

## 3.1.0 - 2026-08-31

- Added an in-app Wi-Fi network drawer using the native Windows WLAN service: saved networks connect directly, while new password entry stays in the Windows network picker.
- Upgraded the Stable update channel from a release-page link to a confirmed download, exact release-asset selection, SHA-256 verification, routing restoration, and installer launch flow.
- Kept Preview useful for inspecting development tags while reserving installable packages for substantial stable releases.
- Added installer maintenance detection for an existing DualLink AppId with visible Update/reinstall, Repair, and Uninstall choices, while preserving the previous install path, Start menu group, and shortcut choices.
- Detected supported multi-process download managers together with their background download engines, so the process that owns the real download connections is routed instead of only its launcher.
- Made setup open DualLink in its existing installer security context, preventing the confusing second elevation failure after installation.

## 3.0.0 - 2026-08-30

- Added accurate selected-application download, upload, connection, and per-route contribution evidence for each boost.
- Added independent live Mbps controls for Ethernet and Wi-Fi, including Off and Full speed; established transfers react without restarting the app, proxy, or filter.
- Made a manual route-speed choice switch to Balanced mode automatically so the user's selected capacities take control immediately.
- Made Balanced routing favor the route with the higher selected speed and kept Smart routing aware of each route's capacity, quality, and active load.
- Added a running-application picker and live target changes that preserve established transfers.
- Kept turned-off routes visible while their existing connections drain, with byte-accurate accounting until completion.
- Strengthened route/filter recovery, secure local proxy authentication, full-path application matching, watchdog validation, bounded connection cleanup, and automatic adapter refresh.
- Simplified the main interface, kept diagnostics on demand, and improved notification-area status and controls.

## 3.0.0-dev.5 - 2026-08-30

- Turning a link off now keeps its existing sessions visible as draining, continues byte-accurate accounting until they finish, and still prevents new connections from using that link.
- Added a 120-connection soak test that proves exact connection accounting and complete client-task cleanup after concurrent traffic.
- Replaced the misleading route-share buttons with independent 0–500 Mbps route controls; Off disables new sessions, Full removes the cap, and every intermediate value throttles that route's active download and upload traffic immediately.
- Made Balanced routing distribute new connections in proportion to each route's live speed setting, while Smart routing uses the same setting in its load score.
- Simplified the connection pane to compact independent Mbps dropdowns plus Automatic and Mode controls, removing the combined total limiter.

## 3.0.0-dev.4 - 2026-08-30

- Application selections now update the filter in place while the local proxy and established downloads keep running; the original recovery state remains intact.

## 3.0.0-dev.3 - 2026-08-30

- Added a plain-language “This boost” summary with byte-accurate download, upload, and successful-connection contribution for each link, plus regression coverage that proves the evidence resets for every boost.

## 3.0.0-dev.2 - 2026-08-30

- Added an on-demand running-application picker with a file-browse fallback, keeping local process discovery out of background polling and documenting its privacy behavior.

## 3.0.0-dev.1 - 2026-08-30

- Opened the DualLink 3.0 development line around accurate app-scoped traffic intelligence, clearer connection evidence, and stronger recovery behavior.
- Kept one shared upload/download bandwidth limit and intentionally excluded per-application setting profiles and settings import/export.
- Added byte-accurate per-route upload and download accounting inside the local proxy, and switched the dashboard, graph, and tray to selected-application traffic while boost is active.
- Added per-route successful-connection evidence so Details and the tray can confirm when both links have actually carried selected-app sessions.
- Replaced session-count guesses in diagnostics with authoritative per-route contribution evidence and a plain explanation of single-connection limits.
- Added duplicate-safe custom application selection and a quiet Remove action for custom targets, without adding per-application settings profiles.

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
- Made development installers, checksums, and SBOM metadata carry the exact preview identity while retaining a Windows-compatible numeric file version.

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
