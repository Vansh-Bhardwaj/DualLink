# DualLink 2.2.0

This release makes DualLink easier to understand at a glance and easier to share with the rest of your household or desktop workload.

## New

- Embedded Inter typography—no font installation required
- Real connected Wi-Fi or hotspot names in the network picker
- Friendly two-line adapter choices instead of internal object names
- Live download and upload activity for each connection
- Download limits at 25, 50, 100, 200, or 300 Mbps
- Plain-language route shares and clearer guidance
- New repository screenshots and connection-flow visual

The download limiter is aggregate across all selected applications. Choose **No limit** for maximum throughput, or choose a value below your normal combined speed to leave room for video, voice calls, remote work, and ordinary browsing. Changes apply live without closing downloads.

## About upload speed

DualLink assigns each TCP connection to one physical link; it cannot split one connection across both links without a remote aggregation server. Speedtest may reuse only a few long-lived connections for its upload phase, and mobile hotspots commonly have much less upstream capacity than downstream capacity. As a result, upload figures may not add as visibly as multi-connection downloads even when both routes are working.

## Installation

Download `DualLink-2.2.0-Setup-x64.exe` and run it as administrator. The release is unsigned, so Windows may display a SmartScreen warning. Verify the installer with `SHA256SUMS.txt`.
