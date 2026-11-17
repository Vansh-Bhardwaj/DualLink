//! Chooses a link for each new connection and opens the connection on it.
//!
//! A connection stays on the link it was opened on for its whole life. Combining comes from
//! spreading many connections over the links, so one connection is never faster than one link.

use crate::limiter::Limiter;
use crate::model::{LinkInfo, LinkStatus, Mode};
use socket2::{Domain, Protocol, SockAddr, Socket, Type};
use std::collections::HashMap;
use std::io;
use std::net::{Ipv4Addr, SocketAddrV4};
use std::sync::atomic::{AtomicU32, AtomicU64, Ordering};
use std::sync::{Arc, Mutex};
use std::time::Duration;
use tokio::net::{TcpSocket, TcpStream};
use tokio::time::Instant;

/// One link as the router should use it.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Route {
    pub info: LinkInfo,
    pub weight: u8,
    pub limit_mbps: u32,
}

#[derive(Clone, Copy, Debug)]
pub struct RouterOptions {
    /// How long one link gets to open a connection before the next is tried.
    pub connect_timeout: Duration,
    /// In Smart mode, how long after switching on only the first link is used, so programs
    /// that sign in at startup do so from one address.
    pub warmup: Duration,
    /// In Smart mode, how long a destination stays on the link it last used.
    pub affinity: Duration,
    pub max_remembered: usize,
}

impl Default for RouterOptions {
    fn default() -> Self {
        RouterOptions {
            connect_timeout: Duration::from_secs(4),
            warmup: Duration::from_secs(15),
            affinity: Duration::from_secs(600),
            max_remembered: 4096,
        }
    }
}

/// Told about the local port of every connection the router opens, before it is used, so a
/// packet engine can leave the router's own traffic alone.
pub trait PortWatcher: Send + Sync {
    fn opened(&self, port: u16);
    fn closed(&self, port: u16);
}

/// Smart mode keeps a destination on the link it used before only while fewer than this many
/// other connections to it are open. A download that opens several connections at once to one
/// server is spread over the links instead; a sign-in, which opens them one after another,
/// stays on one address.
const SPREAD_FROM: u32 = 2;

/// Connections open or opening to each destination.
type OpenCounts = Arc<Mutex<HashMap<SocketAddrV4, u32>>>;

/// One connection's place in [OpenCounts]; dropping it gives the place up.
struct OpenGuard {
    counts: OpenCounts,
    destination: SocketAddrV4,
}

impl OpenGuard {
    /// Counts one more connection to destination and says how many were already open.
    fn new(counts: &OpenCounts, destination: SocketAddrV4) -> (OpenGuard, u32) {
        let mut map = counts.lock().unwrap();
        let entry = map.entry(destination).or_insert(0);
        let before = *entry;
        *entry += 1;
        (OpenGuard { counts: counts.clone(), destination }, before)
    }
}

impl Drop for OpenGuard {
    fn drop(&mut self) {
        let mut map = self.counts.lock().unwrap();
        if let Some(count) = map.get_mut(&self.destination) {
            *count -= 1;
            if *count == 0 {
                map.remove(&self.destination);
            }
        }
    }
}

pub struct Router {
    open: OpenCounts,
    options: RouterOptions,
    watcher: Option<Arc<dyn PortWatcher>>,
    state: Mutex<State>,
    turn: AtomicU64,
}

struct State {
    mode: Mode,
    /// Links in use, in priority order.
    active: Vec<Arc<Link>>,
    /// Every link seen since the router was made, so totals survive a link going away.
    known: HashMap<String, Arc<Link>>,
    remembered: HashMap<SocketAddrV4, (Arc<Link>, Instant)>,
    started: Instant,
}

struct Link {
    id: String,
    route: Mutex<Route>,
    limiter: Limiter,
    accepting: Mutex<bool>,
    active: AtomicU32,
    total: AtomicU64,
    failures: AtomicU32,
    resting_until: Mutex<Option<Instant>>,
    latency_ms: Mutex<Option<f32>>,
    downloaded: AtomicU64,
    uploaded: AtomicU64,
}

impl Link {
    fn new(route: Route) -> Self {
        Link {
            id: route.info.id.clone(),
            limiter: Limiter::new(route.limit_mbps),
            route: Mutex::new(route),
            accepting: Mutex::new(true),
            active: AtomicU32::new(0),
            total: AtomicU64::new(0),
            failures: AtomicU32::new(0),
            resting_until: Mutex::new(None),
            latency_ms: Mutex::new(None),
            downloaded: AtomicU64::new(0),
            uploaded: AtomicU64::new(0),
        }
    }

    fn route(&self) -> Route {
        self.route.lock().unwrap().clone()
    }

    fn weight(&self) -> u32 {
        u32::from(self.route.lock().unwrap().weight.max(1))
    }

    fn healthy(&self, now: Instant) -> bool {
        self.resting_until.lock().unwrap().is_none_or(|until| until <= now)
    }

    /// Counts a connection against the link unless it has just been switched off.
    fn reserve(&self) -> bool {
        let accepting = self.accepting.lock().unwrap();
        if *accepting {
            self.active.fetch_add(1, Ordering::Relaxed);
        }
        *accepting
    }

    fn release(&self) {
        self.active.fetch_sub(1, Ordering::Relaxed);
    }

    fn succeeded(&self, took: Duration) {
        self.failures.store(0, Ordering::Relaxed);
        *self.resting_until.lock().unwrap() = None;
        self.total.fetch_add(1, Ordering::Relaxed);
        let ms = took.as_secs_f32() * 1000.0;
        let mut latency = self.latency_ms.lock().unwrap();
        *latency = Some(latency.map_or(ms, |old| old * 0.75 + ms * 0.25));
    }

    /// Rests the link for 3 s, doubling with each failure in a row up to a minute.
    fn failed(&self) {
        let failures = self.failures.fetch_add(1, Ordering::Relaxed) + 1;
        let secs = (3u64 << (failures - 1).min(5)).min(60);
        *self.resting_until.lock().unwrap() = Some(Instant::now() + Duration::from_secs(secs));
    }

    /// Lower is better: how busy the link is for its share, how slowly it connects, and how
    /// often it has just failed.
    fn score(&self) -> f32 {
        let busy = self.active.load(Ordering::Relaxed) as f32 / self.weight() as f32;
        let latency = self.latency_ms.lock().unwrap().unwrap_or(60.0) / 250.0;
        busy + latency + self.failures.load(Ordering::Relaxed) as f32 * 2.0
    }

    fn status(&self, enabled: bool, now: Instant) -> LinkStatus {
        let route = self.route();
        LinkStatus {
            info: route.info,
            enabled,
            weight: route.weight,
            limit_mbps: route.limit_mbps,
            healthy: self.healthy(now),
            active_connections: self.active.load(Ordering::Relaxed),
            total_connections: self.total.load(Ordering::Relaxed),
            failures: self.failures.load(Ordering::Relaxed),
            latency_ms: *self.latency_ms.lock().unwrap(),
            downloaded: self.downloaded.load(Ordering::Relaxed),
            uploaded: self.uploaded.load(Ordering::Relaxed),
        }
    }
}

/// A connection's hold on its link. Dropping it frees the link's slot.
pub struct Lease {
    link: Arc<Link>,
    port: u16,
    watcher: Option<Arc<dyn PortWatcher>>,
    _open: OpenGuard,
}

impl Lease {
    pub fn link_id(&self) -> &str {
        &self.link.id
    }

    pub async fn throttle(&self, bytes: usize) {
        self.link.limiter.take(bytes).await;
    }

    pub fn downloaded(&self, bytes: usize) {
        self.link.downloaded.fetch_add(bytes as u64, Ordering::Relaxed);
    }

    pub fn uploaded(&self, bytes: usize) {
        self.link.uploaded.fetch_add(bytes as u64, Ordering::Relaxed);
    }
}

impl Drop for Lease {
    fn drop(&mut self) {
        self.link.release();
        if let Some(watcher) = &self.watcher {
            watcher.closed(self.port);
        }
    }
}

impl Router {
    pub fn new(options: RouterOptions, watcher: Option<Arc<dyn PortWatcher>>) -> Self {
        Router {
            open: Arc::new(Mutex::new(HashMap::new())),
            options,
            watcher,
            state: Mutex::new(State {
                mode: Mode::Smart,
                active: Vec::new(),
                known: HashMap::new(),
                remembered: HashMap::new(),
                started: Instant::now(),
            }),
            turn: AtomicU64::new(0),
        }
    }

    /// Replaces the links in use. Connections already open stay where they are; a link left
    /// out takes no new ones.
    pub fn set_routes(&self, routes: Vec<Route>, mode: Mode) {
        let mut state = self.state.lock().unwrap();
        for link in state.known.values() {
            *link.accepting.lock().unwrap() = false;
        }
        let mut active = Vec::with_capacity(routes.len());
        for route in routes {
            let link = match state.known.get(&route.info.id) {
                Some(link) => {
                    link.limiter.set_mbps(route.limit_mbps);
                    let mut current = link.route.lock().unwrap();
                    if current.info.address != route.info.address || current.info.if_index != route.info.if_index {
                        // The adapter came back with a new address: old failures say nothing.
                        link.failures.store(0, Ordering::Relaxed);
                        *link.resting_until.lock().unwrap() = None;
                    }
                    *current = route;
                    drop(current);
                    link.clone()
                }
                None => {
                    let link = Arc::new(Link::new(route));
                    state.known.insert(link.id.clone(), link.clone());
                    link
                }
            };
            *link.accepting.lock().unwrap() = true;
            active.push(link);
        }
        state.remembered.retain(|_, (link, _)| active.iter().any(|a| Arc::ptr_eq(a, link)));
        state.active = active;
        state.mode = mode;
    }

    /// Restarts the Smart-mode warm-up and zeroes the session's byte and connection totals.
    pub fn begin_session(&self) {
        let mut state = self.state.lock().unwrap();
        state.started = Instant::now();
        state.remembered.clear();
        let State { known, active, .. } = &mut *state;
        known.retain(|_, link| active.iter().any(|a| Arc::ptr_eq(a, link)) || link.active.load(Ordering::Relaxed) > 0);
        for link in known.values() {
            link.total.store(0, Ordering::Relaxed);
            link.downloaded.store(0, Ordering::Relaxed);
            link.uploaded.store(0, Ordering::Relaxed);
        }
    }

    pub fn has_routes(&self) -> bool {
        !self.state.lock().unwrap().active.is_empty()
    }

    pub fn active_connections(&self) -> u32 {
        self.state.lock().unwrap().known.values().map(|l| l.active.load(Ordering::Relaxed)).sum()
    }

    /// Links in use first, in priority order, then links that still carry old connections.
    pub fn statuses(&self) -> Vec<LinkStatus> {
        let state = self.state.lock().unwrap();
        let now = Instant::now();
        let mut out: Vec<LinkStatus> = state.active.iter().map(|l| l.status(true, now)).collect();
        let mut retired: Vec<&Arc<Link>> =
            state.known.values().filter(|l| !state.active.iter().any(|a| Arc::ptr_eq(a, l))).collect();
        retired.sort_by(|a, b| a.id.cmp(&b.id));
        out.extend(retired.into_iter().map(|l| l.status(false, now)));
        out
    }

    /// Opens a connection to `destination` on the best link, trying the others if it fails.
    pub async fn connect(&self, destination: SocketAddrV4) -> io::Result<(TcpStream, Lease)> {
        let (open, already) = OpenGuard::new(&self.open, destination);
        let mut open = Some(open);
        let mut last = None;
        // Links can be replaced between choosing and reserving; choose once more if every
        // candidate had just been switched off.
        for _ in 0..2 {
            let mut reserved_any = false;
            for link in self.candidates(destination, already) {
                if !link.reserve() {
                    continue;
                }
                reserved_any = true;
                let started = Instant::now();
                match self.dial(&link, destination).await {
                    Ok((stream, port)) => {
                        link.succeeded(started.elapsed());
                        self.remember(destination, &link);
                        let _open = open.take().expect("taken only on success");
                        return Ok((stream, Lease { link, port, watcher: self.watcher.clone(), _open }));
                    }
                    Err(error) => {
                        link.release();
                        if link_is_at_fault(&error) {
                            link.failed();
                            self.forget(destination, &link);
                        }
                        last = Some(error);
                    }
                }
            }
            if reserved_any {
                break;
            }
        }
        Err(last.unwrap_or_else(|| io::Error::new(io::ErrorKind::NotConnected, "no connection is available")))
    }

    async fn dial(&self, link: &Link, destination: SocketAddrV4) -> io::Result<(TcpStream, u16)> {
        let route = link.route();
        let socket = Socket::new(Domain::IPV4, Type::STREAM, Some(Protocol::TCP))?;
        socket.set_tcp_nodelay(true)?;
        bind_to_adapter(&socket, route.info.if_index)?;
        socket.bind(&SockAddr::from(SocketAddrV4::new(route.info.address, 0)))?;
        let port = socket.local_addr()?.as_socket().map_or(0, |a| a.port());
        socket.set_nonblocking(true)?;
        if let Some(watcher) = &self.watcher {
            watcher.opened(port);
        }
        let socket = TcpSocket::from_std_stream(socket.into());
        let connected = tokio::time::timeout(self.options.connect_timeout, socket.connect(destination.into())).await;
        let result = match connected {
            Ok(Ok(stream)) => return Ok((stream, port)),
            Ok(Err(error)) => error,
            Err(_) => io::Error::new(io::ErrorKind::TimedOut, "the connection timed out"),
        };
        if let Some(watcher) = &self.watcher {
            watcher.closed(port);
        }
        Err(result)
    }

    /// The links to try, best first. lready is how many connections to destination are
    /// open or opening besides this one.
    fn candidates(&self, destination: SocketAddrV4, already: u32) -> Vec<Arc<Link>> {
        let mut state = self.state.lock().unwrap();
        let now = Instant::now();
        let (mut pool, mut resting): (Vec<_>, Vec<_>) = state.active.iter().cloned().partition(|l| l.healthy(now));
        if pool.is_empty() {
            // Everything is resting: try anyway, soonest back first, in case the rest was unlucky.
            resting.sort_by_key(|l| *l.resting_until.lock().unwrap());
            return resting;
        }
        let turn = self.turn.fetch_add(1, Ordering::Relaxed) as usize;
        match state.mode {
            Mode::Backup => {}
            Mode::Balanced => {
                let shares: Vec<usize> =
                    pool.iter().enumerate().flat_map(|(i, l)| std::iter::repeat_n(i, l.weight() as usize)).collect();
                let first = shares[turn % shares.len()];
                pool.rotate_left(first);
            }
            Mode::Smart => {
                let warming = now.duration_since(state.started) < self.options.warmup;
                if !warming {
                    let start = turn % pool.len();
                    pool.rotate_left(start);
                    pool.sort_by(|a, b| a.score().total_cmp(&b.score()));
                    match state.remembered.get(&destination) {
                        Some((link, until)) if *until > now && already < SPREAD_FROM => {
                            if let Some(at) = pool.iter().position(|l| Arc::ptr_eq(l, link)) {
                                let link = pool.remove(at);
                                pool.insert(0, link);
                            }
                        }
                        Some((_, until)) if *until <= now => {
                            state.remembered.remove(&destination);
                        }
                        Some(_) => {}
                        None => {}
                    }
                }
            }
        }
        pool.extend(resting);
        pool
    }

    fn remember(&self, destination: SocketAddrV4, link: &Arc<Link>) {
        let mut state = self.state.lock().unwrap();
        if state.mode != Mode::Smart || state.active.len() < 2 {
            return;
        }
        let now = Instant::now();
        if state.remembered.len() >= self.options.max_remembered {
            state.remembered.retain(|_, (_, until)| *until > now);
            if state.remembered.len() >= self.options.max_remembered
                && let Some(oldest) = state.remembered.iter().min_by_key(|(_, (_, until))| *until).map(|(k, _)| *k) {
                    state.remembered.remove(&oldest);
                }
        }
        state.remembered.insert(destination, (link.clone(), now + self.options.affinity));
    }

    fn forget(&self, destination: SocketAddrV4, link: &Arc<Link>) {
        let mut state = self.state.lock().unwrap();
        if state.remembered.get(&destination).is_some_and(|(l, _)| Arc::ptr_eq(l, link)) {
            state.remembered.remove(&destination);
        }
    }
}

/// Whether a failed connection says the link is bad, as opposed to the destination refusing.
fn link_is_at_fault(error: &io::Error) -> bool {
    use io::ErrorKind::*;
    matches!(
        error.kind(),
        TimedOut | NetworkDown | NetworkUnreachable | HostUnreachable | AddrNotAvailable | AddrInUse | NotConnected
    )
}

/// Ties the socket to one adapter, so two adapters that were handed the same private
/// address range (two tethered phones) still send through the right one.
#[cfg(windows)]
fn bind_to_adapter(socket: &Socket, if_index: u32) -> io::Result<()> {
    use std::os::windows::io::AsRawSocket;
    use windows_sys::Win32::Networking::WinSock::{IP_UNICAST_IF, IPPROTO_IP, SOCKET_ERROR, setsockopt};
    if if_index == 0 {
        return Ok(());
    }
    // Windows takes the interface index in network byte order for IPv4.
    let value = if_index.to_be();
    let result = unsafe {
        setsockopt(
            socket.as_raw_socket() as usize,
            IPPROTO_IP,
            IP_UNICAST_IF,
            (&raw const value).cast(),
            size_of::<u32>() as i32,
        )
    };
    if result == SOCKET_ERROR { Err(io::Error::last_os_error()) } else { Ok(()) }
}

#[cfg(not(windows))]
fn bind_to_adapter(_: &Socket, _: u32) -> io::Result<()> {
    Ok(())
}

/// Whether DualLink should leave a destination alone: this PC, the local network, and
/// addresses that are not on the internet.
pub fn is_local(address: Ipv4Addr) -> bool {
    let o = address.octets();
    address.is_loopback()
        || address.is_private()
        || address.is_link_local()
        || address.is_multicast()
        || address.is_broadcast()
        || address.is_unspecified()
        || (o[0] == 100 && (o[1] & 0xC0) == 64) // carrier-grade NAT, 100.64.0.0/10
        || o[0] >= 240
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::model::LinkKind;
    use std::net::SocketAddr;
    use tokio::io::{AsyncReadExt, AsyncWriteExt};
    use tokio::net::TcpListener;

    fn route(id: &str, address: [u8; 4], weight: u8) -> Route {
        Route {
            info: LinkInfo {
                id: id.into(),
                name: id.into(),
                description: String::new(),
                kind: LinkKind::Ethernet,
                if_index: 0,
                address: address.into(),
                gateway: Ipv4Addr::LOCALHOST,
            },
            weight,
            limit_mbps: 0,
        }
    }

    fn options() -> RouterOptions {
        RouterOptions { warmup: Duration::ZERO, connect_timeout: Duration::from_secs(2), ..Default::default() }
    }

    /// A server that tells each client which address it connected from.
    async fn echo_source() -> SocketAddrV4 {
        let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
        let SocketAddr::V4(address) = listener.local_addr().unwrap() else { unreachable!() };
        tokio::spawn(async move {
            while let Ok((mut stream, peer)) = listener.accept().await {
                tokio::spawn(async move {
                    let _ = stream.write_all(peer.ip().to_string().as_bytes()).await;
                });
            }
        });
        address
    }

    async fn source_of(router: &Router, server: SocketAddrV4) -> String {
        let (mut stream, _lease) = router.connect(server).await.unwrap();
        let mut seen = String::new();
        stream.read_to_string(&mut seen).await.unwrap();
        seen
    }

    #[tokio::test]
    async fn balanced_follows_each_links_share() {
        let server = echo_source().await;
        let router = Router::new(options(), None);
        router.set_routes(vec![route("a", [127, 0, 0, 1], 3), route("b", [127, 0, 0, 2], 1)], Mode::Balanced);
        let mut counts = HashMap::new();
        for _ in 0..40 {
            *counts.entry(source_of(&router, server).await).or_insert(0) += 1;
        }
        assert_eq!(counts["127.0.0.1"], 30);
        assert_eq!(counts["127.0.0.2"], 10);
        let statuses = router.statuses();
        assert_eq!(statuses[0].total_connections, 30);
        assert_eq!(router.active_connections(), 0, "every lease was dropped");
    }

    #[tokio::test]
    async fn a_dead_link_is_skipped_and_rested() {
        let server = echo_source().await;
        let router = Router::new(options(), None);
        // 192.0.2.1 is a documentation address no adapter here owns, so binding to it fails.
        router.set_routes(vec![route("dead", [192, 0, 2, 1], 1), route("live", [127, 0, 0, 2], 1)], Mode::Backup);
        for _ in 0..3 {
            assert_eq!(source_of(&router, server).await, "127.0.0.2");
        }
        let statuses = router.statuses();
        assert!(!statuses[0].healthy && statuses[0].failures == 1, "rested after one failure, then skipped");
        assert_eq!(statuses[1].total_connections, 3);
    }

    #[tokio::test]
    async fn backup_keeps_to_the_first_link() {
        let server = echo_source().await;
        let router = Router::new(options(), None);
        router.set_routes(vec![route("a", [127, 0, 0, 2], 1), route("b", [127, 0, 0, 1], 1)], Mode::Backup);
        for _ in 0..5 {
            assert_eq!(source_of(&router, server).await, "127.0.0.2");
        }
    }

    #[tokio::test]
    async fn smart_keeps_a_destination_on_its_link_and_spreads_others() {
        let first = echo_source().await;
        let router = Router::new(options(), None);
        router.set_routes(
            vec![route("a", [127, 0, 0, 1], 1), route("b", [127, 0, 0, 2], 1), route("c", [127, 0, 0, 3], 1)],
            Mode::Smart,
        );
        let chosen = source_of(&router, first).await;
        for _ in 0..6 {
            assert_eq!(source_of(&router, first).await, chosen, "a destination stays on one link");
        }
        // Held connections make links busy, so new destinations go to the idle ones.
        let mut held = Vec::new();
        let mut used = std::collections::HashSet::new();
        for _ in 0..3 {
            let server = echo_source().await;
            let (mut stream, lease) = router.connect(server).await.unwrap();
            let mut seen = String::new();
            stream.read_to_string(&mut seen).await.unwrap();
            used.insert(seen);
            held.push(lease);
        }
        assert_eq!(used.len(), 3, "three held connections land on three links: {used:?}");
    }

    #[tokio::test]
    async fn smart_spreads_a_download_that_opens_many_connections_to_one_server() {
        let server = echo_source().await;
        let router = Router::new(options(), None);
        router.set_routes(vec![route("a", [127, 0, 0, 1], 1), route("b", [127, 0, 0, 2], 1)], Mode::Smart);
        let mut held = Vec::new();
        for _ in 0..6 {
            held.push(router.connect(server).await.unwrap());
        }
        let statuses = router.statuses();
        assert!(statuses.iter().all(|s| s.active_connections >= 2), "both links carry the download: {statuses:?}");
        drop(held);
        // With the download finished, the next connection goes back to the remembered link.
        let first = source_of(&router, server).await;
        for _ in 0..3 {
            assert_eq!(source_of(&router, server).await, first);
        }
        assert!(router.open.lock().unwrap().is_empty(), "every count was given back");
    }

    #[tokio::test]
    async fn smart_warms_up_on_the_first_link() {
        let router = Router::new(RouterOptions { warmup: Duration::from_secs(60), ..options() }, None);
        router.set_routes(vec![route("a", [127, 0, 0, 2], 1), route("b", [127, 0, 0, 1], 1)], Mode::Smart);
        router.begin_session();
        for _ in 0..4 {
            let server = echo_source().await;
            assert_eq!(source_of(&router, server).await, "127.0.0.2");
        }
    }

    #[tokio::test]
    async fn a_removed_link_takes_nothing_new_but_keeps_its_totals() {
        let server = echo_source().await;
        let router = Router::new(options(), None);
        router.set_routes(vec![route("a", [127, 0, 0, 1], 1), route("b", [127, 0, 0, 2], 1)], Mode::Balanced);
        let (_stream, lease) = router.connect(server).await.unwrap();
        let kept = lease.link_id().to_string();
        let other = if kept == "a" { route("b", [127, 0, 0, 2], 1) } else { route("a", [127, 0, 0, 1], 1) };
        let other_address = other.info.address.to_string();
        router.set_routes(vec![other], Mode::Balanced);
        for _ in 0..4 {
            assert_eq!(source_of(&router, server).await, other_address);
        }
        let retired = router.statuses().into_iter().find(|s| s.info.id == kept).unwrap();
        assert!(!retired.enabled);
        assert_eq!((retired.active_connections, retired.total_connections), (1, 1));
        drop(lease);
        assert_eq!(router.active_connections(), 0);
    }

    #[tokio::test]
    async fn the_watcher_hears_each_port_before_it_connects_and_after_it_closes() {
        #[derive(Default)]
        struct Seen(Mutex<Vec<(u16, bool)>>);
        impl PortWatcher for Seen {
            fn opened(&self, port: u16) {
                self.0.lock().unwrap().push((port, true));
            }
            fn closed(&self, port: u16) {
                self.0.lock().unwrap().push((port, false));
            }
        }
        let server = echo_source().await;
        let seen = Arc::new(Seen::default());
        let router = Router::new(options(), Some(seen.clone()));
        router.set_routes(vec![route("a", [127, 0, 0, 1], 1)], Mode::Balanced);
        let (stream, lease) = router.connect(server).await.unwrap();
        let port = stream.local_addr().unwrap().port();
        assert_eq!(*seen.0.lock().unwrap(), [(port, true)]);
        drop(lease);
        assert_eq!(*seen.0.lock().unwrap(), [(port, true), (port, false)]);
    }

    #[test]
    fn local_destinations_are_recognised() {
        for local in ["127.0.0.1", "10.1.2.3", "192.168.42.129", "172.16.0.1", "169.254.1.1", "224.0.0.251", "100.64.0.1"] {
            assert!(is_local(local.parse().unwrap()), "{local}");
        }
        for public in ["1.1.1.1", "8.8.8.8", "172.32.0.1", "100.128.0.1", "93.184.216.34"] {
            assert!(!is_local(public.parse().unwrap()), "{public}");
        }
    }
}
