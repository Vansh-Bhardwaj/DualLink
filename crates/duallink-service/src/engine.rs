//! The packet engine: watches outgoing packets on the chosen adapters and hands new
//! connections to DualLink's listener.
//!
//! The engine and the router live in this one process. If the process stops for any reason,
//! Windows closes its handle to the packet driver, the adapters return to normal, and nothing
//! is left pointing at a listener that is no longer there.

use crate::procs::{self, Processes, Table};
use duallink_core::nat::{Nat, Verdict};
use duallink_core::packet::{self, UDP};
use duallink_core::router::{PortWatcher, is_local};
use duallink_core::{Config, Scope};
use ndisapi::{
    DataLinkLayerFilter, DirectionFlags, EthRequest, EthRequestMut, FILTER_PACKET_PASS, FILTER_PACKET_REDIRECT,
    FilterFlags, FilterLayerFlags, IPV4, IPV6, IntermediateBuffer, IpAddressV4, IpAddressV6, IpV4Filter,
    IpV4FilterFlags, IpV6Filter, IpV6FilterFlags, Ndisapi, NetworkLayerFilter, NetworkLayerFilterUnion, PortRange,
    StaticFilter, StaticFilterTable, TCPUDP, TcpUdpFilter, TcpUdpFilterFlags, TransportLayerFilter,
    TransportLayerFilterUnion,
};
use std::collections::{HashMap, HashSet};
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex, mpsc};
use std::thread::JoinHandle;
use std::time::{Duration, Instant};
use windows::Win32::Foundation::{CloseHandle, HANDLE, WAIT_OBJECT_0};
use windows::Win32::System::Threading::{CreateEventW, ResetEvent, SetEvent, WaitForMultipleObjects};

const DRIVER: &str = "NDISRD";
const QUIC_PORT: u16 = 443;
/// How long a decision about one UDP port is reused, so a video call's packets are not each
/// looked up in the process table.
const UDP_VERDICT_TTL: Duration = Duration::from_secs(10);

/// What the engine should take over, worked out from the user's configuration.
#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct Policy {
    pub scope: Scope,
    /// Lower-cased program entries.
    pub apps: Vec<String>,
    pub excluded: Vec<String>,
    pub steer_quic: bool,
    pub prefer_ipv4: bool,
    /// Upper-cased adapter GUIDs of the links in use. Other adapters (a VPN, a virtual
    /// switch) are never touched, so their traffic keeps going where it was sent.
    pub adapters: Vec<String>,
}

impl Policy {
    pub fn new(config: &Config, adapters: impl IntoIterator<Item = String>) -> Self {
        let lower = |list: &[String]| list.iter().map(|a| a.trim().to_lowercase()).collect();
        Policy {
            scope: config.scope,
            apps: lower(&config.apps),
            excluded: lower(&config.excluded),
            steer_quic: config.steer_quic,
            prefer_ipv4: config.prefer_ipv4,
            adapters: adapters.into_iter().map(|a| a.to_uppercase()).collect(),
        }
    }

    /// Whether a program's traffic is DualLink's to carry. `path` is lower-case, or `None`
    /// when Windows would not say which program it is.
    pub fn covers(&self, path: Option<&str>) -> bool {
        match (self.scope, path) {
            (Scope::Everything, Some(path)) => !self.excluded.iter().any(|e| procs::matches(path, e)),
            (Scope::Everything, None) => true,
            (Scope::Chosen, Some(path)) => self.apps.iter().any(|a| procs::matches(path, a)),
            (Scope::Chosen, None) => false,
        }
    }
}

/// State shared between the engine thread, the listener and the router.
pub struct Shared {
    pub nat: Mutex<Nat>,
    /// Local ports of connections the router itself opened; never taken over.
    own_ports: Mutex<HashSet<u16>>,
    policy: Mutex<Arc<Policy>>,
    pub routed: AtomicU64,
    pub bypassed: AtomicU64,
}

impl Shared {
    pub fn new(listener_port: u16) -> Self {
        Shared {
            nat: Mutex::new(Nat::new(listener_port)),
            own_ports: Mutex::new(HashSet::new()),
            policy: Mutex::new(Arc::new(Policy::default())),
            routed: AtomicU64::new(0),
            bypassed: AtomicU64::new(0),
        }
    }

    pub fn set_policy(&self, policy: Policy) {
        let mut current = self.policy.lock().unwrap();
        if **current != policy {
            *current = Arc::new(policy);
        }
    }

    fn policy(&self) -> Arc<Policy> {
        self.policy.lock().unwrap().clone()
    }
}

impl PortWatcher for Shared {
    fn opened(&self, port: u16) {
        self.own_ports.lock().unwrap().insert(port);
    }

    fn closed(&self, port: u16) {
        self.own_ports.lock().unwrap().remove(&port);
    }
}

/// A running engine. Dropping it returns every adapter to normal.
pub struct Engine {
    stop: isize,
    thread: Option<JoinHandle<()>>,
}

impl Engine {
    /// Starts the engine, or says in plain words why it could not start.
    pub fn start(shared: Arc<Shared>, log: fn(&str)) -> Result<Engine, String> {
        let stop = unsafe { CreateEventW(None, true, false, None) }.map_err(|e| e.to_string())?;
        let stop_raw = stop.0 as isize;
        let (ready_tx, ready_rx) = mpsc::channel();
        let thread = std::thread::Builder::new()
            .name("packet-engine".into())
            .spawn(move || run(shared, HANDLE(stop_raw as _), ready_tx, log))
            .map_err(|e| e.to_string())?;
        match ready_rx.recv() {
            Ok(Ok(())) => Ok(Engine { stop: stop_raw, thread: Some(thread) }),
            Ok(Err(problem)) => {
                let _ = thread.join();
                unsafe {
                    let _ = CloseHandle(HANDLE(stop_raw as _));
                }
                Err(problem)
            }
            Err(_) => Err("The packet engine stopped while starting.".into()),
        }
    }

    pub fn is_running(&self) -> bool {
        self.thread.as_ref().is_some_and(|t| !t.is_finished())
    }
}

impl Drop for Engine {
    fn drop(&mut self) {
        unsafe {
            let _ = SetEvent(HANDLE(self.stop as _));
        }
        if let Some(thread) = self.thread.take() {
            let _ = thread.join();
        }
        unsafe {
            let _ = CloseHandle(HANDLE(self.stop as _));
        }
    }
}

struct Bound {
    handle: HANDLE,
    name: String,
}

fn run(shared: Arc<Shared>, stop: HANDLE, ready: mpsc::Sender<Result<(), String>>, log: fn(&str)) {
    let driver = match Ndisapi::new(DRIVER) {
        Ok(driver) => driver,
        Err(_) => {
            let _ = ready.send(Err("The network driver is not installed or could not be opened.".into()));
            return;
        }
    };
    let event = match unsafe { CreateEventW(None, true, false, None) } {
        Ok(event) => event,
        Err(error) => {
            let _ = ready.send(Err(format!("Windows could not create an event: {error}")));
            return;
        }
    };
    if let Err(error) = install_filters(&driver) {
        let _ = ready.send(Err(format!("The network driver refused the packet filters: {error}")));
        unsafe {
            let _ = CloseHandle(event);
        }
        return;
    }
    let _ = ready.send(Ok(()));

    let mut bound: Vec<Bound> = Vec::new();
    let mut processes = Processes::default();
    let mut udp_verdicts: HashMap<(Table, u16), (bool, Instant)> = HashMap::new();
    let mut buffer = IntermediateBuffer::default();
    let mut policy = shared.policy();
    let mut reconciled: Option<Instant> = None;
    let own_pid = std::process::id();

    loop {
        let now = Instant::now();
        let latest = shared.policy();
        let policy_changed = !Arc::ptr_eq(&latest, &policy);
        policy = latest;
        if policy_changed || reconciled.is_none_or(|at| now.duration_since(at) >= Duration::from_secs(2)) {
            reconcile(&driver, event, &policy, &mut bound, log);
            shared.nat.lock().unwrap().sweep(now);
            udp_verdicts.retain(|_, (_, at)| now.duration_since(*at) < UDP_VERDICT_TTL);
            reconciled = Some(now);
        }

        let waited = unsafe { WaitForMultipleObjects(&[stop, event], false, 500) };
        if waited == WAIT_OBJECT_0 {
            break;
        }
        // Reset before draining: a packet that arrives while draining sets the event again.
        unsafe {
            let _ = ResetEvent(event);
        }
        for adapter in &bound {
            loop {
                let mut request = EthRequestMut::new(adapter.handle);
                request.set_packet(&mut buffer);
                if driver.read_packet(&mut request).is_err() {
                    break;
                }
                let now = Instant::now();
                let verdict = handle(&shared, &policy, &mut processes, &mut udp_verdicts, &mut buffer, own_pid, now);
                let mut send = EthRequest::new(adapter.handle);
                send.set_packet(&buffer);
                let _ = match verdict {
                    Verdict::Pass => driver.send_packet_to_adapter(&send),
                    Verdict::TurnAround => driver.send_packet_to_mstcp(&send),
                    Verdict::Drop => Ok(()),
                };
            }
        }
    }

    for adapter in &bound {
        let _ = driver.set_adapter_mode(adapter.handle, FilterFlags::default());
    }
    let _ = driver.reset_packet_filter_table();
    unsafe {
        let _ = CloseHandle(event);
    }
    log("Packet engine stopped; adapters are back to normal.");
}

/// Decides one outgoing packet, rewriting the buffer when it is turned around.
fn handle(
    shared: &Shared,
    policy: &Policy,
    processes: &mut Processes,
    udp_verdicts: &mut HashMap<(Table, u16), (bool, Instant)>,
    buffer: &mut IntermediateBuffer,
    own_pid: u32,
    now: Instant,
) -> Verdict {
    if buffer.get_device_flags() != DirectionFlags::PACKET_FLAG_ON_SEND {
        return Verdict::Pass;
    }
    let length = buffer.get_length() as usize;
    let data = buffer.get_data_mut();
    let length = length.min(data.len());
    let frame = &mut data[..length];

    let mut covers = |table: Table, port: u16| match processes.owner(table, port) {
        Some(pid) if pid == own_pid => false,
        Some(pid) => policy.covers(processes.path(pid, now).as_deref()),
        None => policy.covers(None),
    };

    if let Some(v4) = packet::parse_v4(frame) {
        if v4.is_tcp() {
            let verdict = shared.nat.lock().unwrap().outgoing(frame, &v4, now, |v4| {
                // The service's own connections, made for connections it took over, aren't counted.
                if shared.own_ports.lock().unwrap().contains(&v4.source.port()) {
                    return false;
                }
                let take = !is_local(*v4.destination.ip()) && covers(Table::Tcp4, v4.source.port());
                let counter = if take { &shared.routed } else { &shared.bypassed };
                counter.fetch_add(1, Ordering::Relaxed);
                take
            });
            return verdict;
        }
        if v4.protocol == UDP && v4.destination.port() == QUIC_PORT && policy.steer_quic && !is_local(*v4.destination.ip()) {
            let key = (Table::Udp4, v4.source.port());
            let steer = cached(udp_verdicts, key, now, || covers(Table::Udp4, v4.source.port()));
            return if steer { Verdict::Drop } else { Verdict::Pass };
        }
        return Verdict::Pass;
    }

    if let Some(v6) = packet::parse_v6(frame) {
        if !v6.is_global() {
            return Verdict::Pass;
        }
        if v6.opens_connection() && policy.prefer_ipv4 && covers(Table::Tcp6, v6.source_port) {
            let length = packet::refuse_v6(frame);
            buffer.set_length(length as u32);
            return Verdict::TurnAround;
        }
        if v6.protocol == UDP && v6.destination_port == QUIC_PORT && policy.steer_quic {
            let key = (Table::Udp6, v6.source_port);
            let steer = cached(udp_verdicts, key, now, || covers(Table::Udp6, v6.source_port));
            return if steer { Verdict::Drop } else { Verdict::Pass };
        }
    }
    Verdict::Pass
}

fn cached(
    verdicts: &mut HashMap<(Table, u16), (bool, Instant)>,
    key: (Table, u16),
    now: Instant,
    decide: impl FnOnce() -> bool,
) -> bool {
    match verdicts.get(&key) {
        Some((verdict, at)) if now.duration_since(*at) < UDP_VERDICT_TTL => *verdict,
        _ => {
            let verdict = decide();
            verdicts.insert(key, (verdict, now));
            verdict
        }
    }
}

/// Makes the set of watched adapters match the links in use.
fn reconcile(driver: &Ndisapi, event: HANDLE, policy: &Policy, bound: &mut Vec<Bound>, log: fn(&str)) {
    let Ok(adapters) = driver.get_tcpip_bound_adapters_info() else { return };
    let wanted: Vec<(HANDLE, String)> = adapters
        .iter()
        .filter(|a| {
            let name = a.get_name().to_uppercase();
            policy.adapters.iter().any(|guid| name.contains(guid.as_str()))
        })
        .map(|a| (a.get_handle(), a.get_name().to_string()))
        .collect();

    bound.retain(|b| {
        let keep = wanted.iter().any(|(handle, _)| *handle == b.handle);
        if !keep {
            let _ = driver.set_adapter_mode(b.handle, FilterFlags::default());
            log(&format!("Stopped watching adapter {}", b.name));
        }
        keep
    });
    for (handle, name) in wanted {
        if bound.iter().any(|b| b.handle == handle) {
            continue;
        }
        // Only outgoing packets are needed; incoming ones never leave the kernel.
        let watching = driver.set_packet_event(handle, event).is_ok()
            && driver.set_adapter_mode(handle, FilterFlags::MSTCP_FLAG_SENT_TUNNEL).is_ok();
        if watching {
            log(&format!("Watching adapter {name}"));
            bound.push(Bound { handle, name });
        } else {
            log(&format!("Could not watch adapter {name}"));
        }
    }
}

/// Asks the driver to hand over only what DualLink acts on (outgoing TCP, and outgoing UDP
/// to port 443) and to pass everything else itself, so other traffic never visits this process.
fn install_filters(driver: &Ndisapi) -> windows::core::Result<()> {
    const TCP: u8 = 6;
    const UDP: u8 = 17;
    let v4 = |protocol: u8| {
        NetworkLayerFilter::new(
            IPV4,
            NetworkLayerFilterUnion {
                ipv4: IpV4Filter::new(
                    IpV4FilterFlags::IP_V4_FILTER_PROTOCOL,
                    IpAddressV4::default(),
                    IpAddressV4::default(),
                    protocol,
                ),
            },
        )
    };
    let v6 = |protocol: u8| {
        NetworkLayerFilter::new(
            IPV6,
            NetworkLayerFilterUnion {
                ipv6: IpV6Filter::new(
                    IpV6FilterFlags::IP_V6_FILTER_PROTOCOL,
                    IpAddressV6::default(),
                    IpAddressV6::default(),
                    protocol,
                ),
            },
        )
    };
    let to_quic = || {
        TransportLayerFilter::new(
            TCPUDP,
            TransportLayerFilterUnion {
                tcp_udp: TcpUdpFilter::new(
                    TcpUdpFilterFlags::TCPUDP_DEST_PORT,
                    PortRange::default(),
                    PortRange::new(QUIC_PORT, QUIC_PORT),
                    0,
                ),
            },
        )
    };
    let outgoing = |network: NetworkLayerFilter, transport: Option<TransportLayerFilter>| {
        let fields = match transport {
            Some(_) => FilterLayerFlags::NETWORK_LAYER_VALID | FilterLayerFlags::TRANSPORT_LAYER_VALID,
            None => FilterLayerFlags::NETWORK_LAYER_VALID,
        };
        StaticFilter::new(
            0,
            DirectionFlags::PACKET_FLAG_ON_SEND,
            FILTER_PACKET_REDIRECT,
            fields,
            DataLinkLayerFilter::default(),
            network,
            transport.unwrap_or_default(),
        )
    };
    let table = StaticFilterTable::<5>::from_filters([
        outgoing(v4(TCP), None),
        outgoing(v4(UDP), Some(to_quic())),
        outgoing(v6(TCP), None),
        outgoing(v6(UDP), Some(to_quic())),
        StaticFilter::new(
            0,
            DirectionFlags::PACKET_FLAG_ON_SEND_RECEIVE,
            FILTER_PACKET_PASS,
            FilterLayerFlags::empty(),
            DataLinkLayerFilter::default(),
            NetworkLayerFilter::default(),
            TransportLayerFilter::default(),
        ),
    ]);
    driver.set_packet_filter_table(&table)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn policy(scope: Scope) -> Policy {
        let config = Config {
            scope,
            apps: vec!["Steam.exe".into(), r"C:\Tools\Fetch.exe".into()],
            excluded: vec!["Bank.exe".into()],
            ..Config::default()
        };
        Policy::new(&config, ["{d864d3b1-8f4d-4698-aeb2-dd2bc6daf741}".to_string()])
    }

    #[test]
    fn everything_covers_all_but_the_excluded() {
        let p = policy(Scope::Everything);
        assert!(p.covers(Some(r"c:\apps\browser.exe")));
        assert!(!p.covers(Some(r"c:\apps\bank.exe")));
        assert!(p.covers(None), "an unidentified program is still combined");
        assert_eq!(p.adapters, ["{D864D3B1-8F4D-4698-AEB2-DD2BC6DAF741}"]);
    }

    #[test]
    fn chosen_covers_only_the_listed_programs() {
        let p = policy(Scope::Chosen);
        assert!(p.covers(Some(r"d:\games\steam\steam.exe")));
        assert!(p.covers(Some(r"c:\tools\fetch.exe")));
        assert!(!p.covers(Some(r"d:\other\fetch.exe")), "a full path must match exactly");
        assert!(!p.covers(None), "an unidentified program is left alone");
    }
}
