# DualLink 3.0.0

DualLink 3 makes two-link routing easier to control and easier to trust. It shows traffic carried by selected applications, records what each connection contributed during the current boost, and gives Ethernet and Wi-Fi their own live speed choices.

## Highlights

- Set Ethernet and Wi-Fi independently from **Off** through exact Mbps choices to **Full speed**.
- Choosing a manual route speed switches to Balanced mode automatically so the selected capacities guide new connections immediately.
- Change either route while a download is active; the new limit takes effect without restarting DualLink, the launcher, the proxy, or the filter.
- Give Wi-Fi more capacity than Ethernet, or the reverse. Balanced mode assigns more new connections to the route with the higher selected speed.
- Turn a route off without breaking its existing transfers. They drain normally and remain visible in the current-boost evidence.
- See selected-app download, upload, successful connections, and real Ethernet/Wi-Fi contribution instead of unrelated adapter traffic.
- Add a visible running application and change selected targets without interrupting established downloads.
- Use automatic route recovery, secure loopback-only proxy authentication, bounded connection cleanup, and the independent restoration watchdog.

## Important limits

DualLink distributes separate TCP connections. It cannot split one TCP connection across two links without a remote aggregation server. Launchers and browsers benefit when they open multiple connections; live game sessions are intentionally not a target.

The Windows Packet Filter driver bundled by the installer is licensed for personal, educational, and nonprofit use. Commercial use requires a separate license. Release executables are currently unsigned; verify `SHA256SUMS.txt` before running the installer.

## Install

Download `DualLink-3.0.0-Setup-x64.exe`. The offline installer includes all required runtime and routing components. Administrator approval is required because setup installs a local service and network driver. Closing DualLink keeps it in the notification area by default; **Exit and restore** returns the original routing configuration.
