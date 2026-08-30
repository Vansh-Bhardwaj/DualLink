# DualLink 3.0 roadmap

DualLink 3.0 focuses on making combined-routing behavior understandable and dependable for non-technical users. Development checkpoints use `3.0.0-dev.N` tags; a public `3.0.0` GitHub Release is created only after the complete stable gate passes.

## Product direction

1. **Accurate boosted traffic** — distinguish selected-application traffic carried by DualLink from unrelated Windows network activity, including per-route download, upload, and session evidence.
2. **Connection confidence** — explain which links are contributing, when a route is degraded, and why a single transfer may not use both links.
3. **Recovery that stays out of the way** — survive route, filter, sleep, wake, and shutdown failures without rapid retries or leaving altered routing behind.
4. **Lower idle cost** — keep background polling, allocations, helper processes, and retained transfer state bounded while preserving automatic behavior.
5. **Approachable controls** — keep primary actions understandable without exposing IP addresses or technical logs unless requested.

The completed milestone includes app-scoped route accounting, an on-demand running-application picker, an understandable per-boost contribution summary, live application-target updates, accurate draining-route evidence under concurrent load, and immediate independent per-route speed control.

## Intentional exclusions

- No per-application routing-setting profiles. Applications remain simple selectable targets.
- No separate upload limiter and no combined limiter. Each route has one speed control covering its upload and download traffic together.
- No settings import or export.
- No automatic update download or installation.
- No GitHub Release for development tags.

## Stable 3.0 result

The stable milestone proves that app-scoped traffic accounting is correct across both routes, counters remain bounded across repeated sessions, displayed evidence matches actual proxy activity, live target changes preserve established transfers, higher route limits receive more new connections, active limits change without interruption, and unrelated destination failures are not misreported as link failures.
