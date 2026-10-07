# DualLink project audit — 7 October 2026

This records the original `f4a46c7` state. Source line references below refer to that commit. The subsequent changes and current validation are recorded in [the implementation report](IMPLEMENTATION-2026-10-07.md).

DualLink needs a reliability pass followed by a product-focused redesign. Its core proxy has useful safeguards and tests, but the interface, helper lifecycle, and routing promises do not yet form a consistently trustworthy experience. A visual refresh alone would leave several concrete defects intact.

## Current state and evidence

- Version 4.0.0, commit `f4a46c7`; the working tree was clean before this audit.
- Windows desktop application using WPF and .NET 10. The active interface is `ModernMainWindow.xaml`; the project excludes the older `MainWindow.xaml` from compilation.
- Selected processes are redirected by ProxiFyre into a local authenticated SOCKS5 proxy. The elevated helper owns routing and filter changes; the desktop controller remains unelevated. A separate watchdog provides recovery.
- This distributes new TCP connections between source addresses. It cannot combine both links within an existing single TCP connection.
- The desktop/test projects and privileged helper built with zero warnings and errors. The build needed single-node MSBuild and the workload resolver disabled in this restricted environment; the initial default build failed without compiler diagnostics.
- The existing executable test suite completed successfully: 25 PASS checks, including authentication, rotation, affinity, failover, limiter changes, draining, updater verification, and 120 short concurrent connections.
- The socket tests initially hit sandbox error 10013, then passed outside that restriction. Their filter service commands are simulated; passing them does not demonstrate real driver recovery or launcher compatibility.
- Fresh deterministic screenshots were rendered from the current source: main screen at 1080×700, and Details, Settings, and Add application at 940×620. These contain preview data. No actual download, live boost, driver change, or failure injection was performed.

## Three material findings

### 1. Controls and status can diverge from the real routing session

**Evidence**

- `MainWindow.xaml.cs:469` makes start/stop decisions using the local `_boosting` flag. At `:580`, an unhealthy helper snapshot only changes the label to “Recovering…”; it does not reconcile that flag, stop, or schedule a restart. The helper's production health monitor can stop its own session after a failed filter recovery (`PrivilegedServiceHost.cs:472`). If the helper remains connected and returns an inactive snapshot, the controller can remain indefinitely in Recovering without restarting.
- Adapter setters at `MainWindow.xaml.cs:162` save the selected adapter but do not explicitly update helper routes. Manual refresh at `:852` likewise only refreshes the UI. Binding side effects may issue an update in some cases, but there is no reliable command-and-confirmation path for an adapter change. The displayed adapter can differ from the helper's configured source until another operation applies routes.
- `ApplyRouteMix` at `:989` bypasses the controller gate used for start, stop, target changes, and network refresh. Pipe requests are serialized, but that does not make UI lifecycle transitions atomic. Failed updates leave the new selection/settings displayed rather than restoring the last confirmed policy.
- Session totals at `:280` sum only routes accepting new connections. Turning a route off removes its earlier contribution from the session summary even while it drains. Route-level counters are retained by the proxy, so this is a presentation/accounting defect.
- Details displays a hard-coded green “Recovery / Watching” (`ModernMainWindow.xaml:358`). The protocol has no watchdog health field. This is not verified recovery evidence.

**Smallest concrete correction**

Reconcile every helper status response into one controller session state. An unexpected inactive session must enter a bounded recovery transition or an actionable stopped/error state. Serialize configuration commands with lifecycle changes; distinguish requested configuration from acknowledged configuration. Apply adapter changes explicitly and roll back or mark pending on failure. Sum all session routes for totals, and show recovery status only when verified.

Then move this coordination out of the 1,728-line window code-behind into a testable controller. Prefer states such as Idle, Starting, WaitingForApps, Routing, Degraded, Restoring, and Failed, with route contribution represented separately. Keep existing safety mechanisms during the extraction.

### 2. Routing behavior and test coverage do not substantiate the performance promise

**Evidence**

- Smart mode has a 15-second warmup and ten-minute destination affinity (`Routing.cs:23`). Its key is destination IPv4 address plus port (`Socks5Balancer.cs:415`), and each success refreshes expiry (`:560`). Sequential connections to the same download server can therefore stay on one link for the entire download. This protects address-sensitive sessions, but can defeat the primary download-distribution goal. Concurrent first connections can behave differently, so this is workload-dependent, not proof that every download uses one link.
- Smart scoring uses active connection counts, connection latency, and failures (`Socks5Balancer.cs:726`), rather than measured available bandwidth. Balanced mode derives connection shares from configured caps in 50 Mbps buckets, capped at ten shares (`:722`). With both routes at Full speed they receive equal shares regardless of actual capacities. A download-heavy connection and an idle connection count equally.
- The generated filter configuration selects TCP only (`ProxiFyreManager.cs:253`). The proxy only accepts TCP CONNECT and selects an IPv4 destination (`Socks5Balancer.cs:299`, `:411`). UDP/QUIC traffic is outside this routing path; IPv6 destinations are unsupported by this backend. The bundled filter's exact IPv6 interception behavior should be tested and made explicit to avoid disrupting unsupported traffic.
- DNS resolution chooses only the first IPv4 address. It retries routes to that address, not alternative resolved endpoints. An unreachable first endpoint can fail even if another address works.
- A route attempt can wait 12 seconds inside a shared 20-second handshake deadline (`Socks5Balancer.cs:287`, `:434`), so a slow primary consumes much of the backup's opportunity. The relay also cancels both directions when either completes (`:374`); test legitimate TCP half-close behavior before claiming generic application compatibility.
- The two health recovery tests exercise `BoostHealthMonitor`, which is not used by the production helper. Actual health recovery lives in `PrivilegedServiceHost`. Filter target updates are tested with a fake process runner; the real implementation stops and starts ProxiFyre (`ProxiFyreManager.cs:107`). Preservation of established real downloads remains unverified.
- Opening Add application performs process inspection synchronously before showing the drawer (`MainWindow.xaml.cs:1446`, `:1578`). Adapter discovery and rate-interface refresh also run on the UI path. Background running-profile polling has already been moved off-thread; the remaining synchronous paths should receive the same treatment.
- Helper logs use `Trace.WriteLine` (`PrivilegedServiceHost.cs:63`) and are not carried in the status protocol or persisted by an explicit file logger. The UI Activity list cannot explain many connection failures originating in the helper.

**Smallest concrete correction**

Keep compatibility protection, but make its tradeoff explicit and test it against real downloads. Separate routing share/capacity from the speed cap. Establish a supported protocol policy that safely passes through unsupported traffic. Move discovery off the UI thread, retain timestamps with traffic snapshots, and record bounded structured diagnostics from the helper without credentials or destination history by default.

Benchmark before changing the transport engine. Compare direct transfer against proxied transfer on each individual link, then compare both links with sequential and concurrent connections to one and multiple destinations. Measure throughput, CPU, memory, allocations, UI response latency, failed connections, and recovery duration. The existing 120-connection test transfers tiny responses; it is useful cleanup coverage, not a sustained-throughput benchmark.

Microsoft's [WPF threading guidance](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/threading-model) supports keeping dispatcher work short. Current [ProxiFyre documentation](https://github.com/wiresock/proxifyre) describes broader protocol capabilities, but those do not establish support in DualLink's custom backend or the pinned bundled version.

### 3. The interface is styled consistently but communicates configuration better than outcomes

**Evidence**

The rendered main screen devotes its broad central area to equal-height application rows with colored dots, repeated explanatory subtitles, Running/Idle labels, and switches. The throughput metric and tiny graph sit in a narrow 292-pixel utility pane. Actual contribution evidence is buried in Details. Running means a process was found, not that it is being routed or transferring data.

“Automatic,” “Mode,” “Only,” “Full speed,” application switches, and “Enable boost” require the user to infer several overlapping concepts. Status appears in both the title bar and footer, while causes and next actions are mostly hidden. The large metric changes from total adapter traffic to selected-app traffic when boost starts, and the history queue retains samples across that scope change.

The design document already asks for a quiet network instrument, but its Apple/Google/Microsoft synthesis is too broad to decide which information should dominate. The graphite palette and Inter font are reusable; the missing product hierarchy is the larger problem.

**Smallest concrete correction**

Make the main screen answer three questions immediately: Which apps are routed? Are both links contributing? What action is needed now?

- Put one authoritative session status and the primary action together near the top. Use factual wording: “Routing enabled — waiting for new connections,” “Both links carrying traffic,” or “Wi-Fi unavailable — Ethernet continues.”
- Promote selected-app throughput and per-link contribution into the main workspace. Clearly separate device-wide traffic from routed traffic; reset or segment graph history when the scope changes. Offer Mbps/MB/s display if useful for launcher users.
- Use actual application icons and compact rows. Prioritize selected/running apps and show Unselected, Selected but closed, Waiting for traffic, and Routed traffic where the available evidence supports those states. Add search when the list needs it. Installed-app detection should replace the impression that every bundled launcher entry is ready to use.
- Keep adapter choices and essential route state visible. Move speed caps and routing policy to a secondary connection editor; rename Automatic to “Start routing when selected apps run.” Present each mode's actual tradeoff before selection.
- Integrate degraded/error messages with one next action. Use verified watchdog/filter status. Preserve keyboard focus and Windows window conventions; consider standard caption controls and a smaller practical minimum size.

Per-app throughput is a backend feature, not a cosmetic binding change: current protocol counters are per route. Add process attribution before promising precise per-app metrics. Start the redesign with aggregate evidence that already exists.

## Recommended delivery order

| Stage | Work | Completion evidence |
|---|---|---|
| 1. Restore trust | Fix inactive-helper reconciliation, explicit adapter application, command serialization, totals, and unsupported-traffic policy; add durable diagnostics. | Failure injection cannot strand Recovering; displayed policy matches acknowledged routes; totals remain monotonic; failed updates have visible recovery. |
| 2. Establish compatibility and speed | Exercise the production helper and driver with actual downloads; benchmark the proxy; revise affinity and distribution policy using results; remove remaining synchronous discovery. | Repeatable results for each advertised app, documented exceptions, sustained-transfer benchmarks, responsive input during discovery, bounded recovery. |
| 3. Redesign the main workflow | Build the outcome-focused main view using confirmed session data; simplify policy controls; cover loading, waiting, degraded, error, and restore states. | Screenshot and interaction review at minimum size and 100/125/150/200% scaling; understandable status and next action in every state. |
| 4. Strengthen release gates | Extract shared core/controller assemblies, exercise real IPC, add soak and failure tests, publish a compatibility matrix. | Clean install/update/uninstall checks; helper/driver crash and sleep/wake recovery; multi-hour transfers; release claims backed by recorded evidence. |

Architecture should converge on a WPF view/view-model layer, a testable session controller, a shared routing core, the privileged helper, and the watchdog. The service currently links backend source files from the desktop project; extracting those into a shared core will let tests exercise the shipped implementation without depending on the WPF application.

Do not change UI framework as the first response. The observed defects are in coordination, workload assumptions, and information design. Preserve the authenticated loopback endpoint, bounded concurrency, buffer pooling, route accounting, protected recovery state, and checksum verification while fixing those gaps.

## Suggested first implementation slice

Fix helper/UI state reconciliation and adapter updates, add regression coverage through the production controller boundary, and expose confirmed route contribution beside the main action. This is small enough to review and immediately improves both reliability and perceived quality. Defer new features until those results are demonstrated.

This audit identifies implementation defects and risks; it does not establish measured real-world throughput loss or reproduce every reported broken feature. Those require the actual application's workload, two independent internet links, and controlled live tests.
