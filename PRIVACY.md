# Privacy

Effective: 2026-08-28

DualLink does not include analytics, advertising, telemetry, crash upload, user accounts, or a remote service operated by the project.

## Data stored locally

DualLink stores the following under `%LOCALAPPDATA%\DualLink`:

- selected applications and network adapters;
- route weights and UI preferences;
- a temporary recovery record while boosting is active;
- a temporary backup of the pre-existing ProxiFyre configuration, when one exists.

Activity messages are kept in memory for the current session. ProxiFyre may write operational logs under its installation directory. DualLink does not transmit these files.

When the user opens **Add application**, DualLink locally enumerates visible running applications so they can be selected without locating an executable manually. The temporary list contains display names, process filenames, and executable paths, remains in memory, and is not transmitted. Only an application the user adds is stored in settings.

## Network behavior

When armed, DualLink redirects TCP connections created by applications the user explicitly selects to a SOCKS5 balancer running only on `127.0.0.1`. The balancer then creates outbound connections through the selected local adapters to the destination requested by that application. DualLink does not decrypt, inspect, retain, or upload application payloads.

Normal destination services, launchers, browsers, internet providers, hotspot providers, and operating-system components may process network data under their own policies. DualLink does not change those third-party practices.

DualLink makes no background analytics or update requests. When the user explicitly selects **Check now**, it sends a standard HTTPS request to GitHub's public API containing the normal network metadata of a web request and the installed DualLink version as its user agent. When the user explicitly selects **Check connections**, DualLink attempts a short TCP connection from each selected adapter to `1.1.1.1:443` and resolves `example.com` to verify basic internet and DNS reachability. No application payload, settings, activity history, or credentials are included.

## Administrative access

DualLink requests administrator privileges because it controls a local Windows service and writes its local filter configuration. It does not alter Windows privacy settings or collect credentials.

## Removal

Uninstalling DualLink removes the application. Shared prerequisites are left installed to avoid breaking other software. Local settings can be removed manually from `%LOCALAPPDATA%\DualLink` after DualLink is disarmed and closed.
