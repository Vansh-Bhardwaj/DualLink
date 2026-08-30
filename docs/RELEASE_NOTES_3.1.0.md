# DualLink 3.1.0

DualLink 3.1 makes everyday setup and maintenance much simpler while keeping routing changes local and reversible.

## Highlights

- See nearby Wi-Fi networks inside DualLink and connect saved Windows profiles directly. Windows remains responsible for passwords and first-time connections.
- JDownloader 2 is detected together with its bundled Java download engine, so the processes that own download connections are actually routed.
- Stable updates can be checked and installed from Settings. DualLink asks first, accepts only the exact GitHub release installer, verifies its published SHA-256 checksum, restores normal routing, and then starts setup.
- Running setup on an installed copy now offers Update/reinstall, Repair, or Uninstall while preserving the chosen install folder and shortcut preferences.
- Setup can open DualLink after installation without triggering the previous second-elevation failure.

## About single-file download speed

DualLink distributes connections, not packets from one TCP connection. A segmented downloader such as JDownloader can use both routes when the host permits multiple chunks. A host or downloader plugin may enforce one connection; that limitation cannot be bypassed without a remote bonding server.

## Security and privacy

Nearby Wi-Fi details remain on the device, and DualLink never reads or stores Wi-Fi passwords. Stable update downloads are checksum-verified before execution. DualLink still collects no telemetry.
