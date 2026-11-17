//! Keeps what is running in line with what the user asked for and what links exist.

use crate::engine::{Engine, Policy, Shared};
use crate::store::{self, log};
use duallink_core::ipc::{PROTOCOL, Request, Response};
use duallink_core::relay::relay;
use duallink_core::router::{Route, Router, RouterOptions};
use duallink_core::{Config, EngineState, LinkInfo, LinkStatus, Status, links};
use std::net::SocketAddr;
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};
use tokio::net::TcpListener;
use tokio::sync::Notify;

/// How often links are looked for again. Also the longest a lost link goes unnoticed.
const TICK: Duration = Duration::from_secs(2);
/// How long to wait before trying again after the engine fails to start.
const RETRY: Duration = Duration::from_secs(10);

pub struct Supervisor {
    router: Arc<Router>,
    shared: Arc<Shared>,
    inner: Mutex<Inner>,
    changed: Notify,
    started: Instant,
}

struct Inner {
    config: Config,
    engine: Option<Engine>,
    state: EngineState,
    problem: Option<String>,
    retry_at: Option<Instant>,
    links: Vec<LinkInfo>,
}

impl Supervisor {
    /// Opens the listener that taken-over connections arrive on and loads the saved configuration.
    pub async fn new() -> std::io::Result<(Arc<Supervisor>, TcpListener)> {
        let listener = TcpListener::bind("0.0.0.0:0").await?;
        let shared = Arc::new(Shared::new(listener.local_addr()?.port()));
        let router = Arc::new(Router::new(RouterOptions::default(), Some(shared.clone())));
        let supervisor = Arc::new(Supervisor {
            router,
            shared,
            inner: Mutex::new(Inner {
                config: store::load(),
                engine: None,
                state: EngineState::Off,
                problem: None,
                retry_at: None,
                links: Vec::new(),
            }),
            changed: Notify::new(),
            started: Instant::now(),
        });
        Ok((supervisor, listener))
    }

    /// Runs until `stop` is notified, then switches combining off cleanly.
    pub async fn run(self: Arc<Self>, listener: TcpListener, stop: Arc<Notify>) {
        let accepting = tokio::spawn(self.clone().accept(listener));
        loop {
            self.reconcile();
            tokio::select! {
                _ = stop.notified() => break,
                _ = self.changed.notified() => {}
                _ = tokio::time::sleep(TICK) => {}
            }
        }
        accepting.abort();
        self.inner.lock().unwrap().engine = None;
    }

    /// Carries each connection the engine turned around to its real destination.
    async fn accept(self: Arc<Self>, listener: TcpListener) {
        loop {
            let Ok((stream, peer)) = listener.accept().await else {
                tokio::time::sleep(Duration::from_millis(200)).await;
                continue;
            };
            let SocketAddr::V4(peer) = peer else { continue };
            // Anything that was not turned around by the engine is a stranger: close it.
            let Some(destination) = self.shared.nat.lock().unwrap().claim(peer) else { continue };
            let this = self.clone();
            tokio::spawn(async move {
                let _ = stream.set_nodelay(true);
                if let Ok((remote, lease)) = this.router.connect(destination).await {
                    let _ = relay(stream, remote, &lease).await;
                }
                this.shared.nat.lock().unwrap().release(peer, Instant::now());
            });
        }
    }

    /// Finds the links, updates the router, and starts or stops the engine as needed.
    fn reconcile(&self) {
        let found = links::discover().unwrap_or_else(|error| {
            log(&format!("Looking for connections failed: {error}"));
            Vec::new()
        });
        let mut inner = self.inner.lock().unwrap();
        if inner.links != found {
            let names: Vec<String> = found.iter().map(|l| format!("{} ({})", l.name, l.address)).collect();
            log(&format!("Connections: {}", if names.is_empty() { "none".into() } else { names.join(", ") }));
            inner.links = found;
        }

        let routes = routes(&inner.config, &inner.links);
        self.shared.set_policy(Policy::new(&inner.config, routes.iter().map(|r| r.info.id.clone())));
        let have_routes = !routes.is_empty();
        self.router.set_routes(routes, inner.config.mode);

        if !inner.config.enabled {
            if inner.engine.take().is_some() {
                log("Combining switched off.");
            }
            inner.state = EngineState::Off;
            inner.problem = None;
            inner.retry_at = None;
            return;
        }
        if inner.engine.as_ref().is_some_and(|e| !e.is_running()) {
            inner.engine = None;
            inner.problem = Some("The packet engine stopped unexpectedly and is being restarted.".into());
            log("The packet engine stopped unexpectedly.");
        }
        if !have_routes {
            // Nothing to combine: step out of the way entirely until a link returns.
            inner.engine = None;
            inner.state = EngineState::NoLinks;
            return;
        }
        if inner.engine.is_none() {
            if inner.retry_at.is_some_and(|at| Instant::now() < at) {
                return;
            }
            inner.state = EngineState::Starting;
            match Engine::start(self.shared.clone(), log) {
                Ok(engine) => {
                    self.router.begin_session();
                    self.shared.routed.store(0, std::sync::atomic::Ordering::Relaxed);
                    self.shared.bypassed.store(0, std::sync::atomic::Ordering::Relaxed);
                    inner.engine = Some(engine);
                    inner.problem = None;
                    inner.retry_at = None;
                    log("Combining switched on.");
                }
                Err(problem) => {
                    log(&format!("Combining could not start: {problem}"));
                    inner.problem = Some(problem);
                    inner.state = EngineState::Failed;
                    inner.retry_at = Some(Instant::now() + RETRY);
                    return;
                }
            }
        }
        inner.state = EngineState::On;
    }

    pub fn status(&self) -> Status {
        use std::sync::atomic::Ordering::Relaxed;
        let inner = self.inner.lock().unwrap();
        let measured = self.router.statuses();
        let links = inner
            .links
            .iter()
            .map(|info| {
                let choice = inner.config.choice(&info.id);
                measured.iter().find(|m| m.info.id == info.id).cloned().map_or_else(
                    || LinkStatus {
                        info: info.clone(),
                        enabled: choice.enabled,
                        weight: choice.weight,
                        limit_mbps: choice.limit_mbps,
                        healthy: true,
                        active_connections: 0,
                        total_connections: 0,
                        failures: 0,
                        latency_ms: None,
                        downloaded: 0,
                        uploaded: 0,
                    },
                    |m| LinkStatus { info: info.clone(), enabled: choice.enabled, ..m },
                )
            })
            .collect();
        Status {
            version: env!("CARGO_PKG_VERSION").into(),
            state: inner.state,
            problem: inner.problem.clone(),
            config: inner.config.clone(),
            links,
            active_connections: self.router.active_connections(),
            routed_connections: self.shared.routed.load(Relaxed),
            bypassed_connections: self.shared.bypassed.load(Relaxed),
            uptime_ms: self.started.elapsed().as_millis() as u64,
        }
    }

    /// Answers one request from the app.
    pub fn answer(&self, request: Request) -> Response {
        if request.protocol() != PROTOCOL {
            return Response::Error { message: "The app and the service are different versions. Restart DualLink.".into() };
        }
        if let Request::Apply { config, .. } = request {
            if let Err(message) = config.validate() {
                return Response::Error { message };
            }
            if let Err(error) = store::save(&config) {
                log(&format!("The configuration could not be saved: {error}"));
            }
            {
                let mut inner = self.inner.lock().unwrap();
                inner.retry_at = None;
                inner.config = config;
            }
            self.reconcile();
            self.changed.notify_one();
        }
        Response::Ok { status: Box::new(self.status()) }
    }
}

/// The links to use, in the user's order: links they have arranged first, then any others
/// found, leaving out the ones they switched off.
fn routes(config: &Config, found: &[LinkInfo]) -> Vec<Route> {
    let arranged = config.links.iter().filter_map(|c| found.iter().find(|l| l.id.eq_ignore_ascii_case(&c.id)));
    let others = found.iter().filter(|l| !config.links.iter().any(|c| c.id.eq_ignore_ascii_case(&l.id)));
    arranged
        .chain(others)
        .filter_map(|info| {
            let choice = config.choice(&info.id);
            choice.enabled.then(|| Route { info: info.clone(), weight: choice.weight, limit_mbps: choice.limit_mbps })
        })
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;
    use duallink_core::{LinkChoice, LinkKind};

    fn link(id: &str) -> LinkInfo {
        LinkInfo {
            id: id.into(),
            name: id.into(),
            description: String::new(),
            kind: LinkKind::Ethernet,
            if_index: 1,
            address: [10, 0, 0, 2].into(),
            gateway: [10, 0, 0, 1].into(),
        }
    }

    #[test]
    fn links_follow_the_users_order_and_new_ones_join_at_the_end() {
        let config = Config {
            links: vec![
                LinkChoice { weight: 4, ..LinkChoice::new("{WIFI}") },
                LinkChoice { enabled: false, ..LinkChoice::new("{PHONE}") },
                LinkChoice::new("{UNPLUGGED}"),
            ],
            ..Config::default()
        };
        let found = [link("{eth}"), link("{phone}"), link("{wifi}")];
        let routes = routes(&config, &found);
        let ids: Vec<&str> = routes.iter().map(|r| r.info.id.as_str()).collect();
        assert_eq!(ids, ["{wifi}", "{eth}"], "switched-off and missing links are left out");
        assert_eq!((routes[0].weight, routes[1].weight), (4, 1));
    }
}
