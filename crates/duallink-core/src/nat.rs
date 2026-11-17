//! Tracks the connections DualLink has taken over and says what to do with each outgoing packet.
//!
//! A program's connection to `remote` is turned around at the network adapter so that it
//! arrives at DualLink's own listener, appearing to come from `remote`. DualLink then makes
//! the real connection on the link it chooses. Replies from the listener are turned around
//! the same way, so the program sees them coming from `remote`.

use crate::packet::{self, V4};
use std::collections::HashMap;
use std::net::{Ipv4Addr, SocketAddrV4};
use std::time::{Duration, Instant};

/// How long a finished connection's entry stays, so its closing packets still get through.
const LINGER: Duration = Duration::from_secs(30);
/// How long a connection the listener never picked up is kept.
const UNCLAIMED: Duration = Duration::from_secs(60);

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Verdict {
    /// Send the frame on to the network unchanged.
    Pass,
    /// The frame was rewritten in place; deliver it to this PC as if it had arrived.
    TurnAround,
    Drop,
}

#[derive(Clone, Copy, Debug)]
struct Flow {
    local: Ipv4Addr,
    remote: SocketAddrV4,
    claimed: bool,
    /// When the entry may be forgotten.
    expires: Option<Instant>,
    opened: Instant,
}

/// The table of taken-over connections, keyed by the program's local port.
pub struct Nat {
    listener_port: u16,
    flows: HashMap<u16, Flow>,
}

impl Nat {
    pub fn new(listener_port: u16) -> Self {
        Nat { listener_port, flows: HashMap::new() }
    }

    pub fn len(&self) -> usize {
        self.flows.len()
    }

    pub fn is_empty(&self) -> bool {
        self.flows.is_empty()
    }

    /// Decides what happens to one outgoing TCP packet, rewriting `frame` when it is turned
    /// around. `take_over` is asked once per new connection whether DualLink should carry it.
    pub fn outgoing(
        &mut self,
        frame: &mut [u8],
        packet: &V4,
        now: Instant,
        take_over: impl FnOnce(&V4) -> bool,
    ) -> Verdict {
        // A reply from DualLink's listener to a program: make it come from the real remote.
        if packet.source.port() == self.listener_port {
            let port = packet.destination.port();
            return match self.flows.get(&port) {
                Some(flow) if flow.remote.ip() == packet.destination.ip() => {
                    let (remote, local) = (flow.remote, SocketAddrV4::new(flow.local, port));
                    packet::turn_around(frame, packet, remote, local);
                    Verdict::TurnAround
                }
                // The entry is gone; this must not reach the real remote from a port it never saw.
                _ => Verdict::Drop,
            };
        }

        let port = packet.source.port();
        if let Some(flow) = self.flows.get(&port) {
            let same = flow.remote == packet.destination && flow.local == *packet.source.ip();
            if same && !(packet.opens_connection() && flow.expires.is_some()) {
                self.deliver(frame, packet);
                return Verdict::TurnAround;
            }
            if !packet.opens_connection() {
                return Verdict::Pass;
            }
            // Windows reused the port for a new connection.
            self.flows.remove(&port);
        }

        if packet.opens_connection() && take_over(packet) {
            self.flows.insert(
                port,
                Flow { local: *packet.source.ip(), remote: packet.destination, claimed: false, expires: None, opened: now },
            );
            self.deliver(frame, packet);
            return Verdict::TurnAround;
        }
        Verdict::Pass
    }

    fn deliver(&self, frame: &mut [u8], packet: &V4) {
        let from = SocketAddrV4::new(*packet.destination.ip(), packet.source.port());
        let to = SocketAddrV4::new(*packet.source.ip(), self.listener_port);
        packet::turn_around(frame, packet, from, to);
    }

    /// Called when the listener accepts a connection from `peer`: where was it really going?
    /// `None` means the connection was not one DualLink turned around and must be closed.
    pub fn claim(&mut self, peer: SocketAddrV4) -> Option<SocketAddrV4> {
        let flow = self.flows.get_mut(&peer.port())?;
        if flow.remote.ip() != peer.ip() || flow.claimed {
            return None;
        }
        flow.claimed = true;
        Some(flow.remote)
    }

    /// Called when the listener's side of a connection has ended.
    pub fn release(&mut self, peer: SocketAddrV4, now: Instant) {
        if let Some(flow) = self.flows.get_mut(&peer.port())
            && flow.remote.ip() == peer.ip() {
                flow.expires = Some(now + LINGER);
            }
    }

    /// Forgets entries whose time is up. Call every few seconds.
    pub fn sweep(&mut self, now: Instant) {
        self.flows.retain(|_, flow| match flow.expires {
            Some(expires) => expires > now,
            None => flow.claimed || now.duration_since(flow.opened) < UNCLAIMED,
        });
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::packet::tests::tcp_frame;
    use crate::packet::{ACK, SYN, parse_v4};

    const LISTENER: u16 = 40000;

    fn step(nat: &mut Nat, frame: &mut [u8], now: Instant, take: bool) -> Verdict {
        let packet = parse_v4(frame).unwrap();
        nat.outgoing(frame, &packet, now, |_| take)
    }

    #[test]
    fn a_connection_is_carried_both_ways() {
        let mut nat = Nat::new(LISTENER);
        let now = Instant::now();

        // The program opens a connection; it is delivered to the listener as if from the remote.
        let mut syn = tcp_frame("192.168.1.5:50000", "93.184.216.34:443", SYN, &[]);
        assert_eq!(step(&mut nat, &mut syn, now, true), Verdict::TurnAround);
        let seen = parse_v4(&syn).unwrap();
        assert_eq!(seen.source, "93.184.216.34:50000".parse().unwrap());
        assert_eq!(seen.destination, format!("192.168.1.5:{LISTENER}").parse().unwrap());

        // The listener learns where the connection was going, once.
        let peer = "93.184.216.34:50000".parse().unwrap();
        assert_eq!(nat.claim(peer), Some("93.184.216.34:443".parse().unwrap()));
        assert_eq!(nat.claim(peer), None);

        // The listener's reply reaches the program as if from the remote's real port.
        let mut reply = tcp_frame(&format!("192.168.1.5:{LISTENER}"), "93.184.216.34:50000", SYN | ACK, &[]);
        assert_eq!(step(&mut nat, &mut reply, now, false), Verdict::TurnAround);
        let seen = parse_v4(&reply).unwrap();
        assert_eq!(seen.source, "93.184.216.34:443".parse().unwrap());
        assert_eq!(seen.destination, "192.168.1.5:50000".parse().unwrap());

        // Later packets from the program follow without asking again.
        let mut data = tcp_frame("192.168.1.5:50000", "93.184.216.34:443", ACK, b"GET /");
        assert_eq!(step(&mut nat, &mut data, now, false), Verdict::TurnAround);
        assert_eq!(parse_v4(&data).unwrap().destination.port(), LISTENER);
    }

    #[test]
    fn connections_not_taken_over_pass_untouched() {
        let mut nat = Nat::new(LISTENER);
        let mut syn = tcp_frame("192.168.1.5:50001", "93.184.216.34:443", SYN, &[]);
        let before = syn.clone();
        assert_eq!(step(&mut nat, &mut syn, Instant::now(), false), Verdict::Pass);
        assert_eq!(syn, before);
        // A connection that was already open when DualLink started is never picked up midway.
        let mut data = tcp_frame("192.168.1.5:50002", "93.184.216.34:443", ACK, b"x");
        assert_eq!(step(&mut nat, &mut data, Instant::now(), true), Verdict::Pass);
        assert!(nat.is_empty());
    }

    #[test]
    fn a_stray_listener_packet_never_reaches_the_internet() {
        let mut nat = Nat::new(LISTENER);
        let mut stray = tcp_frame(&format!("192.168.1.5:{LISTENER}"), "93.184.216.34:50000", ACK, &[]);
        assert_eq!(step(&mut nat, &mut stray, Instant::now(), true), Verdict::Drop);
    }

    #[test]
    fn a_stranger_connecting_to_the_listener_is_not_claimed() {
        let mut nat = Nat::new(LISTENER);
        let mut syn = tcp_frame("192.168.1.5:50000", "93.184.216.34:443", SYN, &[]);
        step(&mut nat, &mut syn, Instant::now(), true);
        assert_eq!(nat.claim("203.0.113.9:50000".parse().unwrap()), None, "right port, wrong address");
        assert_eq!(nat.claim("93.184.216.34:50009".parse().unwrap()), None, "unknown port");
    }

    #[test]
    fn entries_linger_for_closing_packets_then_go() {
        let mut nat = Nat::new(LISTENER);
        let start = Instant::now();
        let mut syn = tcp_frame("192.168.1.5:50000", "93.184.216.34:443", SYN, &[]);
        step(&mut nat, &mut syn, start, true);
        let peer = "93.184.216.34:50000".parse().unwrap();
        nat.claim(peer);
        nat.release(peer, start);

        let mut fin = tcp_frame("192.168.1.5:50000", "93.184.216.34:443", ACK, &[]);
        assert_eq!(step(&mut nat, &mut fin, start + Duration::from_secs(5), false), Verdict::TurnAround);
        nat.sweep(start + Duration::from_secs(29));
        assert_eq!(nat.len(), 1);
        nat.sweep(start + Duration::from_secs(31));
        assert!(nat.is_empty());
    }

    #[test]
    fn a_reused_port_starts_a_fresh_connection() {
        let mut nat = Nat::new(LISTENER);
        let now = Instant::now();
        let mut first = tcp_frame("192.168.1.5:50000", "93.184.216.34:443", SYN, &[]);
        step(&mut nat, &mut first, now, true);
        let peer = "93.184.216.34:50000".parse().unwrap();
        nat.claim(peer);
        nat.release(peer, now);

        let mut second = tcp_frame("192.168.1.5:50000", "1.1.1.1:80", SYN, &[]);
        assert_eq!(step(&mut nat, &mut second, now, true), Verdict::TurnAround);
        assert_eq!(nat.claim("1.1.1.1:50000".parse().unwrap()), Some("1.1.1.1:80".parse().unwrap()));
        assert_eq!(nat.len(), 1);
    }

    #[test]
    fn an_unclaimed_entry_is_dropped_after_a_minute() {
        let mut nat = Nat::new(LISTENER);
        let start = Instant::now();
        let mut syn = tcp_frame("192.168.1.5:50000", "93.184.216.34:443", SYN, &[]);
        step(&mut nat, &mut syn, start, true);
        nat.sweep(start + Duration::from_secs(59));
        assert_eq!(nat.len(), 1);
        nat.sweep(start + Duration::from_secs(61));
        assert!(nat.is_empty());
    }
}
