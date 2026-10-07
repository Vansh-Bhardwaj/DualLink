# Privacy

Effective: 2026-10-07

DualLink does not include analytics, advertising, telemetry, crash upload, user accounts, or a remote service operated by the project.

## Data stored locally

DualLink stores the following under `%LOCALAPPDATA%\DualLink`:

- selected applications and network adapters;
- route weights and UI preferences.

While boosting is active, the elevated local helper keeps a temporary recovery record and, when needed, a backup of the pre-existing filter configuration under `%PROGRAMDATA%\DualLink\Recovery`. That folder is restricted to Administrators and SYSTEM. The helper deletes both files after routing is restored.

Activity messages are displayed in memory and also written to bounded local diagnostic logs: `%LOCALAPPDATA%\DualLink\app.log` and `%PROGRAMDATA%\DualLink\Recovery\service.log`. Each log keeps at most two approximately 512 KiB files. Messages can include local app, network, path, and error details. Connection-open messages omit destination addresses, and DualLink does not log application payloads or proxy credentials. ProxiFyre may write its own operational logs under its installation directory. DualLink does not transmit these files.

At startup and on **Scan**, DualLink checks local Windows app registrations, known install folders, and running supported processes. It reads executable icons for display. The discovered app list stays in memory; selected app names and custom executable paths are saved locally. DualLink does not scan entire drives or upload the inventory.

When the user opens **Add**, DualLink locally enumerates visible running applications so they can be selected without locating an executable manually. The temporary list contains display names, process filenames, and executable paths, remains in memory, and is not transmitted. Only an application the user adds is stored in settings.

When the user opens **Wi-Fi networks**, DualLink asks Windows for nearby network names, signal strength, and saved-profile status. This information stays on the device. DualLink never reads or stores Wi-Fi passwords; Windows handles credentials for new networks.

## Network behavior

When armed, DualLink redirects TCP connections created by applications the user explicitly selects to a SOCKS5 balancer running only on `127.0.0.1`. The balancer then creates outbound connections through the selected local adapters to the destination requested by that application. DualLink does not decrypt, inspect, retain, or upload application payloads.

Normal destination services, launchers, browsers, internet providers, hotspot providers, and operating-system components may process network data under their own policies. DualLink does not change those third-party practices.

DualLink makes no background analytics or update requests. When the user explicitly selects **Check now**, it sends a standard HTTPS request to GitHub's public API containing the normal network metadata of a web request and the installed DualLink version as its user agent. If the user confirms an update, the installer and checksum manifest are downloaded from the GitHub release to `%LOCALAPPDATA%\DualLink\Updates`. When the user explicitly selects **Check connections**, DualLink attempts a short TCP connection from each selected adapter to `1.1.1.1:443` and resolves `example.com` to verify basic internet and DNS reachability. No application payload, settings, activity history, or credentials are included.

## Administrative access

The DualLink interface does not run as administrator. Windows asks for approval only after the user selects **Start**; a separate local helper then controls the packet-filter service and writes its temporary filter configuration. The helper exits with the interface and does not alter Windows privacy settings or collect account credentials.

## Removal

Uninstalling DualLink removes the application. Shared prerequisites are left installed to avoid breaking other software. Local settings can be removed manually from `%LOCALAPPDATA%\DualLink` after DualLink is disarmed and closed.
