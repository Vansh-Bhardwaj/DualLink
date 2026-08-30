# Security policy

## Supported versions

Only the latest published release receives security fixes.

## Reporting a vulnerability

Use the repository's private **Security → Report a vulnerability** form. Do not publish exploit details in a regular issue before a fix is available.

Include the affected version, Windows version, reproduction steps, expected impact, and any relevant local logs with secrets removed. Never include passwords, tokens, private keys, browser profiles, or unrelated network captures.

## Security boundaries

DualLink runs with administrator privileges and controls a packet-filter service. Install only release artifacts published by this repository and verify their SHA-256 checksum. Release binaries are currently not Authenticode-signed, so Windows SmartScreen reputation is not guaranteed.

DualLink is not a VPN, firewall, anonymity service, or encryption product. It changes the source adapter used for selected outbound TCP connections; end-to-end security remains the responsibility of the selected application and destination protocol.

The in-app stable updater accepts only HTTPS GitHub release links, requires the exact installer entry in the published `SHA256SUMS.txt`, verifies the downloaded installer with SHA-256 before launch, and asks for confirmation before installation. Preview builds are shown for inspection but are not installed automatically.
