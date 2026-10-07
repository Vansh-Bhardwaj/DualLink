# DualLink changes and validation — 7 October 2026

The audit led to changes in the interface, app discovery, controller, shared routing core, helper, tests, and documentation. These changes are packaged as stable version 4.1.0; version 4.0.0 is not being republished.

## Delivered behavior

- The main screen is links, speed, and apps, with Start/Stop. App descriptions and explanatory paragraphs are removed. Settings, limits, diagnostics, and logs are secondary drawers. Drawers have a backdrop, Escape dismissal, keyboard focus, and contained Tab navigation.
- Startup scans Windows App Paths/uninstall registrations, known folders, and supported running executables. Only existing supported apps appear. The scan does not walk drives. Scan refreshes the inventory, Add handles running/custom executables, and executable icons are extracted off the UI thread and cached as frozen images.
- This PC's scan found Brave, Edge, Epic Games, and Steam, with real icons. Missing launchers were absent. Custom apps remain available, and an active selected profile retains its acknowledged executable paths during a scan.
- Helper snapshots reconcile a stopped session with the controller. Filter recovery has a bounded 15-second reconciliation window. Failed route changes restore the confirmed mode and limits. Start/Stop, adapter updates, Wi-Fi changes, and target changes share the lifecycle gate; rapid route edits are coalesced.
- Corrupt, mismatched, timed-out, or canceled in-flight helper responses discard the pipe. Operation rejections remain retryable. Emergency helper termination leaves the independent watchdog alive. The helper reports the watchdog's actual process state and restarts it when needed.
- Session totals include disabled/draining routes. Graphs reset between device and routed-app traffic. Helper sampling timestamps drive routed rate calculations. An open process is labelled Open rather than presented as evidence of traffic.
- App/adapter discovery, statistics capture, icon extraction, executable metadata, and settings writes run away from the UI thread. The app list virtualizes. Settings are serialized and atomically replaced; diagnostic logs are bounded and persisted locally.
- Desktop and helper consume one shared routing-core assembly. Connection sharing follows route weights independently of bandwidth caps. Fresh settings use Both (Balanced); existing modes remain compatible. DNS retries multiple IPv4 answers. Request-side half-close preserves the response until it finishes or the session is canceled.
- The filter explicitly supports IPv4 TCP. ProxiFyre's unsupported-address-family behavior blocks selected-app IPv6 to allow IPv4 fallback; it does not provide IPv6 routing. UDP/QUIC remain outside the routed path. This behavior follows the pinned [ProxiFyre 2.5.0 configuration contract](https://github.com/wiresock/proxifyre/blob/v2.5.0/docs/configuration.md).

## Automated validation

A framework-dependent review build is available in `dist/review`: run `DualLink.exe` with all files kept together. It includes the helper and watchdog and requires the .NET 10 Windows Desktop runtime plus the existing filter/driver prerequisites. It is not a published installer. The bundled app was smoke-tested through its read-only snapshot mode.

Release builds cover the desktop, shared core, privileged helper, watchdog, integration tests, window tests, and benchmark. The final builds use single-node MSBuild with the workload resolver disabled in this environment. Restores use cached packages with NuGet auditing disabled because its network endpoint is unavailable here; this is not a dependency vulnerability assessment.

The integration executable contains 33 passing checks. They cover authentication, route rotation, destination affinity, quarantine/failover, byte accounting, live bandwidth updates, disabled-route draining, a 120-connection soak, updater checksums, simulated filter recovery, DNS fallback, half-close responses, independent caps, installed-app and protected-process detection, and real named-pipe client failure handling.

The seven window checks exercise the production WPF controller against synthetic snapshots and a fake pipe helper: inactive-session recovery, retained totals, truthful watchdog display, graph scope changes, prevention of device-speed spikes after stopping, real icon caching, and rejected-route rollback. Test settings go to a temporary fixture. These checks are wired into Windows CI and the build script.

Screenshots are reviewed at 1000×680 and the 860×580 minimum: installed app icons, main, settings, expanded limits, details, Add, Wi-Fi, Off, waiting, failed start, one link, and empty apps. Traffic is synthetic preview data. Screenshot rendering does not establish Windows multi-monitor DPI, screen-reader, or physical network behavior.

## Performance baseline

The benchmark transfers 64 MiB per connection through Windows loopback, directly and through the shared proxy, at 1, 8, and 32 concurrent connections. It excludes a warm-up trial and records three measured trials. Every byte and the proxy's download accounting are checked. Raw results are in [performance-baseline.json](performance-baseline.json).

The current run's approximate median proxy throughput is 7.0, 39.9, and 19.6 Gbit/s for 1, 8, and 32 connections. CPU and allocations include client, server, and proxy in one harness process. At 32 connections the direct harness is faster, so proxy overhead is still measurable. Short local transfers are noisy; these numbers establish a repeatable baseline, not an internet speed claim or proof of improvement over the previous build.

Run it again with:

```powershell
dotnet run --project tests/DualLink.Benchmarks -c Release -- docs/performance-baseline.json
```

## Live validation and remaining coverage

During the implementation audit the installed ProxiFyre service was queried read-only and was stopped. The automated filter operations are simulated; the UI tests use a fake helper. After installation, the user supplied a live screenshot with DualLink On, Edge selected, both Ethernet and Wi-Fi in use, and nonzero traffic totals on each route. That records a live two-link session; it does not establish a controlled speed comparison or broad app compatibility.

Remaining coverage includes a compatibility matrix for detected apps in Both/Safe/Backup, sustained direct/single-link/dual-link comparisons, actual filter and helper crashes, network loss/reconnect, sleep/wake, IPv4 fallback, clean stop/exit restoration, multi-hour transfers, and launcher sign-in. The user requested stable publication after their local test; these remaining scenarios are disclosed rather than described as passed. App discovery is not proof that a server opens enough connections to benefit from both links. Precise per-app throughput is also not implemented: current counters remain per route.

The window still owns substantial controller code. The recovery policy and routing backend are shared/testable, but a complete view-model/controller extraction remains a future architectural change.
