# DualLink privileged service

DualLink v4 keeps the normal WPF controller unelevated. Only the small
`DualLink.Service` process carries the administrator manifest, because the
packet-filter integration and its Windows service controls require elevation.

## Local boundary

The controller and helper communicate through a per-session Windows named pipe:

- the pipe name is generated for each launch and must use the `DualLink.Service.`
  prefix;
- the pipe ACL grants read/write access to the requesting Windows user and
  full control to LocalSystem and built-in administrators;
- the helper impersonates the connected client and rejects any SID other than
  the requesting user;
- frames are UTF-8 JSON lines, capped at 256 KiB, with a protocol version and
  request identifier; responses must match both before the client accepts them;
- no TCP listener, remote endpoint, VPN, relay, account, or telemetry channel
  is exposed by this boundary.

The shared contract lives in `DualLink.Service.Protocol`. It intentionally has
no WPF or Windows networking implementation dependency, so the UI can use it
without inheriting administrator behavior.

## Session safety

The helper validates routes, application matchers, ports, limits, credentials,
and request sizes before touching the filter configuration. A session starts
the local SOCKS balancer first, then writes the application filter target. If
either step fails, it stops the balancer and attempts to restore the previous
configuration.

When the pipe closes, the controller exits, the helper receives `shutdown`, or
the packet filter cannot be recovered, the helper restores the prior
configuration and exits. The existing recovery state and watchdog remain the
last-resort recovery path when Windows stops the process at an inopportune
time.

Established connections stay on their selected route. Route updates affect
only new connections, so changing a limit cannot move an existing TCP stream
between source addresses.

## Integration sequence

1. Ship `DualLink.Service.exe` beside the UI. The service executable is the
   only component that requests elevation.
2. The unelevated UI calls `DualLinkServiceClient.StartElevatedAsync` only for
   an explicit user action that needs a privileged session, or connects to an
   already-running, installer-managed instance with `ConnectAsync`.
3. Send `start` with route definitions, application matchers, and the local
   proxy credentials. Use `update-routes` for live route changes and
   `update-targets` for a changed application selection.
4. Poll `status` from the UI and call `stop` before shutdown. Disposing the
   client also sends a best-effort `shutdown`; a disconnected pipe still
   triggers the helper's fail-safe restore.

The offline installer ships `DualLink.Service.exe` beside the controller and
watchdog. The helper is started on demand through an explicit elevation request;
it is not a persistent Windows service and the installer does not start it.
The WPF executable uses an `asInvoker` manifest, so an idle controller does not
request administrator approval.
