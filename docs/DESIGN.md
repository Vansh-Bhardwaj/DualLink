# DualLink interface contract

## User and task

A Windows user should be able to pick their apps, choose links, and press Start without knowing network terminology. Use short words that name actions. Keep explanations, marketing, protocol details, and internal implementation out of the main screen.

## Main view

- A 272 px link pane shows Ethernet and Wi-Fi, their current state, speed, and downloaded bytes. Use only and Use both are direct actions.
- The main pane shows confirmed session state beside Start/Stop, a large speed value, a one-minute graph, and evidence of which links contributed.
- Show Device speed while stopped and App speed while routing. Clear graph history when switching scope. Never present an open process as evidence of routed traffic.
- Apps come from supported installations on this device, with their executable icons. Do not pre-fill missing launchers. Preserve custom apps, selected choices, and Add. Scan refreshes detection.
- App rows contain an icon, name, Open/Closed state, and switch. No app descriptions or promotional subtitles. Use a quiet initial only if an icon cannot be read.
- Optional routing mode, speed limits, tray preferences, and updates belong in Settings. Diagnostics and technical activity belong in Details.

## Visual rules

Use embedded Inter, graphite surfaces, hairline separators, and one restrained indigo action color. Amber identifies Ethernet and cyan identifies Wi-Fi. Use spacing and type hierarchy rather than nested cards. App names are 14 px; speed is 40 px; supporting labels are 12 px. Preserve visible keyboard focus, 34 px control targets, contrast, automation names, and hover/pressed/disabled states.

The default window is 1000×680 and the minimum is 860×580. App lists virtualize and scroll. Drawers are 380 px wide, have a subdued backdrop, contain keyboard navigation, and close with Escape or a click outside. Keep the close control visible even when drawer content scrolls.

## Required states

| State | Main action / evidence |
|---|---|
| Off | Start, Device speed |
| Waiting for app | Stop, selected apps retained |
| Waiting for traffic | Stop, no invented contribution |
| On | Stop, confirmed filter/session, link byte counts |
| Ethernet only / Wi-Fi only | Remaining link named; disabled link says Off |
| Reconnecting | Bounded recovery; failed restore retains Stop |
| Couldn't start / Couldn't stop | One short next action |
| Scanning / empty apps | Scanning… / No apps found, Scan and Add |
| Rejected settings | Last confirmed mode and limits restored, Change failed |

## Finish gate

Render and inspect main, settings, expanded limits, details, app picker, Wi-Fi, and idle/waiting/error/single-link/empty states at minimum size. Verify installed icons on the actual device. Keep synthetic screenshots labelled Preview. Test real window reconciliation and failed-change rollback against a fake helper, then run the shared routing regressions. Physical driver recovery, sleep/wake, long downloads, and actual launcher compatibility require live validation; screenshots and loopback tests cannot establish those results.
