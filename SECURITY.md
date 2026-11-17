# Security policy

## Supported versions

Only the latest published release receives security fixes.

## Reporting a vulnerability

Use the repository's private **Security → Report a vulnerability** form. Do not publish exploit details in a regular issue before a fix is available.

Include the affected version, Windows version, reproduction steps, expected impact, and any relevant local logs with secrets removed. Never include passwords, tokens, private keys, browser profiles, or unrelated network captures.

## Security boundaries

The DualLink service runs as a Windows service with system rights; it owns the packet driver and carries combined connections. The window runs with ordinary user permissions and controls the service over a local named pipe that refuses remote clients and is open only to Administrators, SYSTEM and users signed in at the PC. Every request is size-limited and checked before it is applied, and the service saves its configuration only after it validates it.

Combined connections reach the service on a local listener. The service accepts a connection there only when it turned that exact connection around itself, matching both address and port; it closes anything else at once. Setup adds a Windows Firewall rule that lets the service accept these connections.

If the service stops for any reason, Windows closes its handle to the packet driver and the adapters return to normal; no traffic is left pointing at a part that is gone. Windows restarts the service after a failure.

DualLink is not a VPN, firewall, anonymity service, or encryption product. It changes which connection carries outgoing TCP connections; end-to-end security remains the responsibility of each program and the protocol it uses.

Install only release artifacts published by this repository and verify their SHA-256 checksum. Release binaries are currently not Authenticode-signed, so Windows SmartScreen reputation is not guaranteed. DualLink 5 does not update itself; **Check online** opens the releases page.
