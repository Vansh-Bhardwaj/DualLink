# Privacy

Effective: 2026-11-17

DualLink does not include analytics, advertising, telemetry, crash upload, user accounts, or a remote service operated by the project.

## Data stored locally

The DualLink service stores, under `%PROGRAMDATA%\DualLink`:

- `config.json`: whether combining is on, the sharing mode, which programs to combine or leave alone, and each connection's share, speed limit, order and on/off choice;
- `service.log`: what the service did, such as starting, the connections it found, and combining switching on or off. It holds at most about 1 MiB before it starts a fresh file, keeping one older copy. It contains adapter names and local addresses. It never contains the destinations programs connect to or anything they send.

The window stores its own preferences (appearance, and whether to stay in the notification area) in `%LOCALAPPDATA%\DualLink\app.json`. If you choose **Open when Windows starts**, DualLink adds a value under `HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run`.

To show programs, the window reads the names and icons of programs you list and of programs that have a window open on screen. That list stays in memory; only the programs you add are saved. DualLink does not scan your drives.

## Network behavior

While combining is on, the service looks at the first packet of each new outgoing TCP connection on the connections in use, and at outgoing UDP packets to port 443, to decide whether the connection is one to combine. It asks Windows which program opened the connection. For connections it combines, it opens the same connection to the same destination over the connection it picks and passes the bytes through unchanged. DualLink does not decrypt, inspect, keep, or upload what programs send.

Destination services, internet providers, hotspot providers and Windows itself may process network data under their own policies. DualLink does not change those practices.

DualLink makes no background requests of its own. **Check online** in Settings opens DualLink's releases page in your browser.

## Administrative access

Installing needs administrator approval once: setup installs the network driver, the DualLink service and a Windows Firewall rule. The service runs as a Windows service with system rights. The window runs with your normal permissions and asks nothing of Windows on its own.

## Removal

Uninstalling DualLink removes the window, the service, its firewall rule, its folder under `%PROGRAMDATA%\DualLink` and its sign-in entry. The Windows Packet Filter driver and the Visual C++ runtime stay installed, because other software may use them. `%LOCALAPPDATA%\DualLink\app.json` can be deleted by hand.
