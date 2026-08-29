# DualLink interface contract

## Product and task

DualLink is a compact network-control utility for Windows users downloading large games and files through two independent links. The primary task is to choose applications and turn distribution on or off with immediate confidence that both links are healthy.

## Reference direction

The interface follows the information discipline of TripMode's application list and the layered hierarchy described by Apple's macOS Human Interface Guidelines, without copying Apple assets or pretending to be a macOS window.

- TripMode press kit: https://tripmode.ch/press-kit/
- Apple materials guidance: https://developer.apple.com/design/human-interface-guidelines/materials
- Apple sidebar guidance: https://developer.apple.com/design/human-interface-guidelines/sidebars
- Little Snitch Mini: https://apps.apple.com/us/app/little-snitch-mini/id1629008763

## Visual direction: quiet network instrument

- Use one graphite content plane and one subtly lighter navigation/utility plane.
- Reserve translucent or raised material for controls and transient drawers, not every content row.
- Eliminate card nesting, uppercase eyebrow labels, neon outlines, and oversized danger actions.
- Use the embedded Inter type family for its screen-focused proportions and high small-size legibility. It provides the calm neo-grotesque character associated with contemporary Apple and Google interfaces without redistributing proprietary SF Pro or Google Sans assets.
- Prefer Regular for prose, Medium for labels and application names, and SemiBold only for exceptional emphasis. Avoid walls of bold white text.
- Use a restrained indigo action color. Ethernet amber and Wi-Fi cyan identify physical links only.
- Use hairline separators and grouped list rows instead of bordered cards.
- Use geometry-based monochrome icons with a consistent 16-pixel optical size.

## Layout and reading order

1. Window identity and live state.
2. Combined throughput and the two contributing links.
3. Applications participating in distribution.
4. One compact Boost/Restore control.
5. Diagnostics and preferences in on-demand inspectors.

The left utility pane remains 292 pixels wide. The application list receives all remaining width. At minimum width, labels truncate and the list scrolls; controls do not shrink below their target size.

## Components

- **Status capsule:** live dot, plain-language state, no border when idle.
- **Link row:** adapter selector, speed, small proportional weight stepper, and an Only command.
- **Application row:** colored identity mark, name, description, running state, and a switch at the trailing edge.
- **Boost control:** compact two-state button. Red is reserved for Restore while active.
- **Inspector:** right-edge sheet for diagnostics or settings, with grouped rows and logs.

## State requirements

- Default routing: neutral state and Boost action.
- Armed/waiting: amber state with target switches retained.
- Boosting: green state, live combined speed, session count.
- Single-link mode: active state names the remaining link.
- Recovering: amber state and automatic service restart.
- Missing filter or adapters: explicit non-green state and actionable diagnostics.
- Running versus idle applications: quiet status text; selection remains independent.

## Finish gate

- No clipped text at 940×620 or the 1080×700 reference size.
- Text and controls meet WCAG AA contrast against their immediate surfaces.
- Every interactive control has hover, pressed, disabled, and keyboard-focus feedback.
- No functional information relies on color alone.
- Details and settings remain hidden until requested.
- Screenshot review must show a clear primary action and no repeated decorative container treatment.
